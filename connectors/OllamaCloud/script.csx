using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class Script : ScriptBase
{
    private const string OllamaHost = "https://ollama.com";
    private const int MaxBatchSize = 10;

    public override async Task<HttpResponseMessage> ExecuteAsync()
    {
        string operationId = DecodeOperationId(this.Context.OperationId);

        return operationId switch
        {
            "GenerateChatResponse" => await HandleGenerateChatResponseAsync().ConfigureAwait(false),
            "GenerateTextResponse" => await HandleGenerateTextResponseAsync().ConfigureAwait(false),
            "GenerateEmbeddings" => await HandleGenerateEmbeddingsAsync().ConfigureAwait(false),
            "ListModels" => await HandleListModelsAsync().ConfigureAwait(false),
            "GenerateBatchChatResponses" => await HandleGenerateBatchChatResponsesAsync().ConfigureAwait(false),
            _ => CreateErrorResponse(HttpStatusCode.BadRequest, "UNKNOWN_OPERATION", "Unknown operation: " + operationId, null)
        };
    }

    private async Task<HttpResponseMessage> HandleGenerateChatResponseAsync()
    {
        RequestBodyReadResult requestReadResult = await TryReadRequestBodyAsync().ConfigureAwait(false);
        if (!requestReadResult.IsSuccess)
        {
            return requestReadResult.ErrorResponse;
        }

        JObject requestBody = requestReadResult.Body;

        JObject payload;
        HttpResponseMessage errorResponse;
        if (!TryPrepareChatPayload(requestBody, out payload, out errorResponse))
        {
            return errorResponse;
        }

        ExecutionResult executionResult = await ExecuteJsonRequestAsync(HttpMethod.Post, "/api/chat", payload, ResponseTransformKind.Chat).ConfigureAwait(false);
        return CreateExecutionResponse(executionResult);
    }

    private async Task<HttpResponseMessage> HandleGenerateTextResponseAsync()
    {
        RequestBodyReadResult requestReadResult = await TryReadRequestBodyAsync().ConfigureAwait(false);
        if (!requestReadResult.IsSuccess)
        {
            return requestReadResult.ErrorResponse;
        }

        JObject requestBody = requestReadResult.Body;

        JObject payload;
        HttpResponseMessage errorResponse;
        if (!TryPrepareGeneratePayload(requestBody, out payload, out errorResponse))
        {
            return errorResponse;
        }

        ExecutionResult executionResult = await ExecuteJsonRequestAsync(HttpMethod.Post, "/api/generate", payload, ResponseTransformKind.Generate).ConfigureAwait(false);
        return CreateExecutionResponse(executionResult);
    }

    private async Task<HttpResponseMessage> HandleGenerateEmbeddingsAsync()
    {
        RequestBodyReadResult requestReadResult = await TryReadRequestBodyAsync().ConfigureAwait(false);
        if (!requestReadResult.IsSuccess)
        {
            return requestReadResult.ErrorResponse;
        }

        JObject requestBody = requestReadResult.Body;

        JObject payload;
        HttpResponseMessage errorResponse;
        if (!TryPrepareEmbedPayload(requestBody, out payload, out errorResponse))
        {
            return errorResponse;
        }

        ExecutionResult executionResult = await ExecuteJsonRequestAsync(HttpMethod.Post, "/api/embed", payload, ResponseTransformKind.None).ConfigureAwait(false);
        return CreateExecutionResponse(executionResult);
    }

    private async Task<HttpResponseMessage> HandleListModelsAsync()
    {
        ExecutionResult executionResult = await ExecuteJsonRequestAsync(HttpMethod.Get, "/api/tags", null, ResponseTransformKind.None).ConfigureAwait(false);
        return CreateExecutionResponse(executionResult);
    }

    private async Task<HttpResponseMessage> HandleGenerateBatchChatResponsesAsync()
    {
        RequestBodyReadResult requestReadResult = await TryReadRequestBodyAsync().ConfigureAwait(false);
        if (!requestReadResult.IsSuccess)
        {
            return requestReadResult.ErrorResponse;
        }

        JObject requestBody = requestReadResult.Body;
        HttpResponseMessage errorResponse;

        JArray requests = requestBody["requests"] as JArray;
        if (requests == null || requests.Count == 0)
        {
            return CreateErrorResponse(HttpStatusCode.BadRequest, "MISSING_REQUESTS", "The 'requests' array is required and must contain at least one chat request.", null);
        }

        if (requests.Count > MaxBatchSize)
        {
            return CreateErrorResponse(HttpStatusCode.BadRequest, "BATCH_TOO_LARGE", "Batch requests are limited to " + MaxBatchSize + " items per action.", new JObject { ["maxBatchSize"] = MaxBatchSize });
        }

        bool stopOnError = requestBody["stopOnError"]?.ToObject<bool>() ?? false;
        var results = new JArray();
        int successCount = 0;
        int failureCount = 0;

        for (int i = 0; i < requests.Count; i++)
        {
            if (!(requests[i] is JObject batchItem))
            {
                failureCount++;
                results.Add(new JObject
                {
                    ["index"] = i,
                    ["isSuccess"] = false,
                    ["statusCode"] = (int)HttpStatusCode.BadRequest,
                    ["error"] = CreateStandardErrorDetails("INVALID_BATCH_ITEM", "Each batch item must be a JSON object.")
                });

                if (stopOnError)
                {
                    break;
                }

                continue;
            }

            JObject payload;
            if (!TryPrepareChatPayload((JObject)batchItem.DeepClone(), out payload, out errorResponse))
            {
                failureCount++;
                results.Add(CreateBatchErrorResult(i, errorResponse));

                if (stopOnError)
                {
                    break;
                }

                continue;
            }

            ExecutionResult executionResult = await ExecuteJsonRequestAsync(HttpMethod.Post, "/api/chat", payload, ResponseTransformKind.Chat).ConfigureAwait(false);
            bool isSuccess = ((int)executionResult.StatusCode >= 200) && ((int)executionResult.StatusCode < 300);

            if (isSuccess)
            {
                successCount++;
            }
            else
            {
                failureCount++;
            }

            results.Add(CreateBatchExecutionResult(i, executionResult));

            if (!isSuccess && stopOnError)
            {
                break;
            }
        }

        var responseBody = new JObject
        {
            ["totalRequests"] = requests.Count,
            ["successCount"] = successCount,
            ["failureCount"] = failureCount,
            ["results"] = results
        };

        return CreateJsonResponse(HttpStatusCode.OK, responseBody);
    }

    private async Task<RequestBodyReadResult> TryReadRequestBodyAsync()
    {
        string content = this.Context.Request.Content == null
            ? null
            : await this.Context.Request.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(content))
        {
            return RequestBodyReadResult.FromError(CreateErrorResponse(HttpStatusCode.BadRequest, "EMPTY_REQUEST", "Request body is required.", null));
        }

        try
        {
            return RequestBodyReadResult.FromBody(JObject.Parse(content));
        }
        catch (JsonException ex)
        {
            return RequestBodyReadResult.FromError(CreateErrorResponse(HttpStatusCode.BadRequest, "INVALID_JSON", "Request body is not valid JSON: " + ex.Message, null));
        }
    }

    private bool TryPrepareChatPayload(JObject requestBody, out JObject payload, out HttpResponseMessage errorResponse)
    {
        errorResponse = ValidateStructuredOutputRequest(requestBody);
        if (errorResponse != null)
        {
            payload = null;
            return false;
        }

        payload = PrepareBasePayload(requestBody);

        if (string.IsNullOrWhiteSpace(payload["model"]?.ToString()))
        {
            errorResponse = CreateErrorResponse(HttpStatusCode.BadRequest, "MISSING_MODEL", "The 'model' field is required.", null);
            return false;
        }

        JArray messages = payload["messages"] as JArray;
        if (messages == null || messages.Count == 0)
        {
            errorResponse = CreateErrorResponse(HttpStatusCode.BadRequest, "MISSING_MESSAGES", "The 'messages' array is required and must contain at least one message.", null);
            return false;
        }

        if (!ValidateNoStreaming(payload, out errorResponse))
        {
            return false;
        }

        return true;
    }

    private bool TryPrepareGeneratePayload(JObject requestBody, out JObject payload, out HttpResponseMessage errorResponse)
    {
        errorResponse = ValidateStructuredOutputRequest(requestBody);
        if (errorResponse != null)
        {
            payload = null;
            return false;
        }

        payload = PrepareBasePayload(requestBody);

        if (string.IsNullOrWhiteSpace(payload["model"]?.ToString()))
        {
            errorResponse = CreateErrorResponse(HttpStatusCode.BadRequest, "MISSING_MODEL", "The 'model' field is required.", null);
            return false;
        }

        if (string.IsNullOrWhiteSpace(payload["prompt"]?.ToString()))
        {
            errorResponse = CreateErrorResponse(HttpStatusCode.BadRequest, "MISSING_PROMPT", "The 'prompt' field is required.", null);
            return false;
        }

        if (!ValidateNoStreaming(payload, out errorResponse))
        {
            return false;
        }

        return true;
    }

    private bool TryPrepareEmbedPayload(JObject requestBody, out JObject payload, out HttpResponseMessage errorResponse)
    {
        payload = (JObject)requestBody.DeepClone();
        errorResponse = null;

        MergeExtraBody(payload);
        payload.Remove("extraBody");

        if (string.IsNullOrWhiteSpace(payload["model"]?.ToString()))
        {
            errorResponse = CreateErrorResponse(HttpStatusCode.BadRequest, "MISSING_MODEL", "The 'model' field is required.", null);
            return false;
        }

        JToken inputs = payload["inputs"];
        JToken inputText = payload["inputText"];

        if (inputs is JArray inputArray && inputArray.Count > 0)
        {
            payload["input"] = inputArray;
        }
        else if (inputText != null && inputText.Type == JTokenType.String && !string.IsNullOrWhiteSpace(inputText.ToString()))
        {
            payload["input"] = inputText.ToString();
        }
        else
        {
            errorResponse = CreateErrorResponse(HttpStatusCode.BadRequest, "MISSING_INPUT", "Provide either 'inputText' or a non-empty 'inputs' array.", null);
            return false;
        }

        if (payload["keepAlive"] != null)
        {
            payload["keep_alive"] = payload["keepAlive"];
        }

        payload.Remove("inputText");
        payload.Remove("inputs");
        payload.Remove("keepAlive");

        return true;
    }

    private HttpResponseMessage ValidateStructuredOutputRequest(JObject requestBody)
    {
        string formatMode = requestBody["formatMode"]?.ToString();
        if (!string.Equals(formatMode, "schema", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!(requestBody["responseSchema"] is JObject))
        {
            return CreateErrorResponse(HttpStatusCode.BadRequest, "MISSING_RESPONSE_SCHEMA", "The 'responseSchema' object is required when 'formatMode' is set to 'schema'.", null);
        }

        return null;
    }

    private JObject PrepareBasePayload(JObject requestBody)
    {
        JObject payload = (JObject)requestBody.DeepClone();
        MergeExtraBody(payload);
        ApplyCommonMappings(payload);
        CleanupHelperFields(payload);
        return payload;
    }

    private void MergeExtraBody(JObject payload)
    {
        JObject extraBody = payload["extraBody"] as JObject;
        if (extraBody == null)
        {
            return;
        }

        foreach (JProperty property in extraBody.Properties())
        {
            payload[property.Name] = property.Value.DeepClone();
        }
    }

    private void ApplyCommonMappings(JObject payload)
    {
        JObject options = payload["options"] as JObject;
        if (options == null)
        {
            options = new JObject();
        }
        else
        {
            options = (JObject)options.DeepClone();
        }

        ApplyOptionAlias(payload, options, "maxTokens", "num_predict");
        ApplyOptionAlias(payload, options, "temperature", "temperature");
        ApplyOptionAlias(payload, options, "topP", "top_p");
        ApplyOptionAlias(payload, options, "topK", "top_k");
        ApplyOptionAlias(payload, options, "minP", "min_p");
        ApplyOptionAlias(payload, options, "typicalP", "typical_p");
        ApplyOptionAlias(payload, options, "seed", "seed");
        ApplyOptionAlias(payload, options, "repeatPenalty", "repeat_penalty");
        ApplyOptionAlias(payload, options, "presencePenalty", "presence_penalty");
        ApplyOptionAlias(payload, options, "frequencyPenalty", "frequency_penalty");
        ApplyOptionAlias(payload, options, "repeatLastN", "repeat_last_n");
        ApplyOptionAlias(payload, options, "numCtx", "num_ctx");
        ApplyOptionAlias(payload, options, "numThread", "num_thread");
        ApplyOptionAlias(payload, options, "numKeep", "num_keep");
        ApplyOptionAlias(payload, options, "stop", "stop");

        if (options.Properties().Any())
        {
            payload["options"] = options;
        }
        else
        {
            payload.Remove("options");
        }

        if (payload["keepAlive"] != null)
        {
            payload["keep_alive"] = payload["keepAlive"];
        }

        string formatMode = payload["formatMode"]?.ToString();
        if (!string.IsNullOrWhiteSpace(formatMode))
        {
            if (string.Equals(formatMode, "json", StringComparison.OrdinalIgnoreCase))
            {
                payload["format"] = "json";
            }
            else if (string.Equals(formatMode, "schema", StringComparison.OrdinalIgnoreCase) && payload["responseSchema"] is JObject responseSchema)
            {
                payload["format"] = responseSchema.DeepClone();
            }
        }
    }

    private void CleanupHelperFields(JObject payload)
    {
        string[] helperFields = new[]
        {
            "extraBody",
            "keepAlive",
            "formatMode",
            "responseSchema",
            "maxTokens",
            "topP",
            "topK",
            "minP",
            "typicalP",
            "repeatPenalty",
            "presencePenalty",
            "frequencyPenalty",
            "repeatLastN",
            "numCtx",
            "numThread",
            "numKeep"
        };

        foreach (string helperField in helperFields)
        {
            payload.Remove(helperField);
        }
    }

    private void ApplyOptionAlias(JObject payload, JObject options, string inputName, string optionName)
    {
        JToken token = payload[inputName];
        if (token == null || token.Type == JTokenType.Null)
        {
            return;
        }

        options[optionName] = token.DeepClone();
    }

    private bool ValidateNoStreaming(JObject payload, out HttpResponseMessage errorResponse)
    {
        errorResponse = null;
        bool streamRequested = payload["stream"]?.ToObject<bool>() ?? false;
        if (streamRequested)
        {
            errorResponse = CreateErrorResponse(HttpStatusCode.BadRequest, "UNSUPPORTED_STREAMING", "Streaming responses are not supported by this connector. Omit 'stream' or set it to false.", null);
            return false;
        }

        payload["stream"] = false;
        return true;
    }

    private async Task<ExecutionResult> ExecuteJsonRequestAsync(HttpMethod method, string relativePath, JObject payload, ResponseTransformKind transformKind)
    {
        var request = new HttpRequestMessage(method, new Uri(new Uri(OllamaHost), relativePath));
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        string authorization = GetAuthorizationHeader();
        if (string.IsNullOrWhiteSpace(authorization))
        {
            return ExecutionResult.FromError(HttpStatusCode.Unauthorized, CreateStandardErrorBody("MISSING_API_KEY", "An Ollama API key is required. Configure the connector authorization value before calling this operation."));
        }

        request.Headers.TryAddWithoutValidation("Authorization", authorization);

        if (payload != null)
        {
            request.Content = new StringContent(payload.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
        }

        try
        {
            HttpResponseMessage upstreamResponse = await this.Context.SendAsync(request, this.CancellationToken).ConfigureAwait(false);
            return await ExecutionResult.FromHttpResponseAsync(upstreamResponse, transformKind).ConfigureAwait(false);
        }
        catch (TaskCanceledException ex)
        {
            return ExecutionResult.FromError(HttpStatusCode.RequestTimeout, CreateStandardErrorBody("REQUEST_TIMEOUT", "The request to Ollama timed out: " + ex.Message));
        }
        catch (HttpRequestException ex)
        {
            return ExecutionResult.FromError(HttpStatusCode.BadGateway, CreateStandardErrorBody("UPSTREAM_REQUEST_FAILED", "The request to Ollama failed: " + ex.Message));
        }
    }

    private HttpResponseMessage CreateExecutionResponse(ExecutionResult executionResult)
    {
        if (executionResult.JsonBody != null)
        {
            return CreateJsonResponse(executionResult.StatusCode, executionResult.JsonBody);
        }

        var response = new HttpResponseMessage(executionResult.StatusCode)
        {
            Content = new StringContent(executionResult.RawContent ?? string.Empty, Encoding.UTF8)
        };

        if (!string.IsNullOrWhiteSpace(executionResult.ContentType))
        {
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(executionResult.ContentType);
        }

        return response;
    }

    private JObject CreateBatchExecutionResult(int index, ExecutionResult executionResult)
    {
        var result = new JObject
        {
            ["index"] = index,
            ["isSuccess"] = ((int)executionResult.StatusCode >= 200) && ((int)executionResult.StatusCode < 300),
            ["statusCode"] = (int)executionResult.StatusCode
        };

        if (executionResult.JsonBody != null)
        {
            if (result["isSuccess"]?.ToObject<bool>() == true)
            {
                result["response"] = executionResult.JsonBody.DeepClone();
            }
            else
            {
                result["error"] = ExtractErrorPayload(executionResult.JsonBody);
            }
        }
        else
        {
            result[result["isSuccess"]?.ToObject<bool>() == true ? "responseText" : "errorText"] = executionResult.RawContent ?? string.Empty;
        }

        return result;
    }

    private JObject CreateBatchErrorResult(int index, HttpResponseMessage errorResponse)
    {
        string content = errorResponse.Content.ReadAsStringAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        JObject body;

        try
        {
            body = JObject.Parse(content);
        }
        catch (JsonException)
        {
            body = new JObject
            {
                ["error"] = CreateStandardErrorDetails("INVALID_BATCH_ITEM", content)
            };
        }

        return new JObject
        {
            ["index"] = index,
            ["isSuccess"] = false,
            ["statusCode"] = (int)errorResponse.StatusCode,
            ["error"] = ExtractErrorPayload(body)
        };
    }

    private string GetAuthorizationHeader()
    {
        IEnumerable<string> values;
        if (!this.Context.Request.Headers.TryGetValues("Authorization", out values))
        {
            return null;
        }

        string value = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        return "Bearer " + value.Trim();
    }

    private static JObject TryParseJsonString(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            return JObject.Parse(content);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JToken TryParseEmbeddedJson(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        if (!(trimmed.StartsWith("{") || trimmed.StartsWith("[")))
        {
            return null;
        }

        try
        {
            return JToken.Parse(trimmed);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JObject ApplyResponseTransform(JObject body, ResponseTransformKind transformKind)
    {
        if (body == null)
        {
            return null;
        }

        if (transformKind == ResponseTransformKind.Chat)
        {
            JObject message = body["message"] as JObject;
            string content = message?["content"]?.ToString();
            JToken parsedContent = TryParseEmbeddedJson(content);
            if (parsedContent != null)
            {
                body["parsedMessageContent"] = parsedContent;
            }
        }
        else if (transformKind == ResponseTransformKind.Generate)
        {
            string response = body["response"]?.ToString();
            JToken parsedResponse = TryParseEmbeddedJson(response);
            if (parsedResponse != null)
            {
                body["parsedResponse"] = parsedResponse;
            }
        }

        return body;
    }

    private static string DecodeOperationId(string operationId)
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

    private static JObject CreateStandardErrorBody(string code, string message)
    {
        return new JObject
        {
            ["error"] = CreateStandardErrorDetails(code, message)
        };
    }

    private static JObject CreateStandardErrorDetails(string code, string message)
    {
        return new JObject
        {
            ["code"] = code,
            ["message"] = message
        };
    }

    private static JToken ExtractErrorPayload(JObject body)
    {
        if (body == null)
        {
            return null;
        }

        return body["error"] != null ? body["error"].DeepClone() : body.DeepClone();
    }

    private HttpResponseMessage CreateErrorResponse(HttpStatusCode statusCode, string code, string message, JObject details)
    {
        JObject errorBody = CreateStandardErrorBody(code, message);
        if (details != null)
        {
            ((JObject)errorBody["error"])["details"] = details;
        }

        return CreateJsonResponse(statusCode, errorBody);
    }

    private HttpResponseMessage CreateJsonResponse(HttpStatusCode statusCode, JObject body)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json")
        };
    }

    private enum ResponseTransformKind
    {
        None,
        Chat,
        Generate
    }

    private sealed class ExecutionResult
    {
        public HttpStatusCode StatusCode { get; private set; }
        public JObject JsonBody { get; private set; }
        public string RawContent { get; private set; }
        public string ContentType { get; private set; }

        public static async Task<ExecutionResult> FromHttpResponseAsync(HttpResponseMessage response, ResponseTransformKind transformKind)
        {
            string content = response.Content == null
                ? string.Empty
                : await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            JObject jsonBody = TryParseJsonString(content);
            if (jsonBody != null)
            {
                jsonBody = ApplyResponseTransform(jsonBody, transformKind);
            }

            string contentType = response.Content?.Headers?.ContentType?.MediaType;
            return new ExecutionResult
            {
                StatusCode = response.StatusCode,
                JsonBody = jsonBody,
                RawContent = jsonBody == null ? content : null,
                ContentType = contentType
            };
        }

        public static ExecutionResult FromError(HttpStatusCode statusCode, JObject errorBody)
        {
            return new ExecutionResult
            {
                StatusCode = statusCode,
                JsonBody = errorBody,
                RawContent = null,
                ContentType = "application/json"
            };
        }
    }

    private sealed class RequestBodyReadResult
    {
        public JObject Body { get; private set; }

        public HttpResponseMessage ErrorResponse { get; private set; }

        public bool IsSuccess
        {
            get { return this.ErrorResponse == null; }
        }

        public static RequestBodyReadResult FromBody(JObject body)
        {
            return new RequestBodyReadResult
            {
                Body = body
            };
        }

        public static RequestBodyReadResult FromError(HttpResponseMessage errorResponse)
        {
            return new RequestBodyReadResult
            {
                ErrorResponse = errorResponse
            };
        }
    }
}
