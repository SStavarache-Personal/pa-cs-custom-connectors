using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class Script : ScriptBase
{
    private static readonly Regex PlaceholderRegex = new Regex(
        @"\{\{\s*(?<name>[A-Za-z0-9_]+)\s*\}\}",
        RegexOptions.Compiled
    );

    public override async Task<HttpResponseMessage> ExecuteAsync()
    {
        string operationId = DecodeOperationId(this.Context.OperationId);

        return operationId switch
        {
            "RenderEmailTemplate" => await HandleRenderEmailTemplateAsync().ConfigureAwait(false),
            _ => CreateErrorResponse(
                HttpStatusCode.BadRequest,
                "UNKNOWN_OPERATION",
                $"Unknown operation: {operationId}",
                null
            )
        };
    }

    private async Task<HttpResponseMessage> HandleRenderEmailTemplateAsync()
    {
        JObject requestBody;
        try
        {
            string content = await this.Context.Request.Content
                .ReadAsStringAsync()
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(content))
            {
                return CreateErrorResponse(
                    HttpStatusCode.BadRequest,
                    "INVALID_REQUEST",
                    "Request body is required.",
                    null
                );
            }

            requestBody = JObject.Parse(content);
        }
        catch (JsonException ex)
        {
            return CreateErrorResponse(
                HttpStatusCode.BadRequest,
                "INVALID_JSON",
                "Request body is not valid JSON: " + ex.Message,
                null
            );
        }

        string subjectTemplate = requestBody["subjectTemplate"]?.ToString();
        string bodyHtmlTemplate = requestBody["bodyHtmlTemplate"]?.ToString();
        string deduplicationKeyTemplate = requestBody["deduplicationKeyTemplate"]?.ToString();
        JObject variableDefinition = requestBody["variableDefinition"] as JObject;
        JObject context = requestBody["context"] as JObject;

        if (string.IsNullOrWhiteSpace(subjectTemplate))
        {
            return CreateErrorResponse(HttpStatusCode.BadRequest, "INVALID_REQUEST", "The 'subjectTemplate' field is required.", null);
        }

        if (string.IsNullOrWhiteSpace(bodyHtmlTemplate))
        {
            return CreateErrorResponse(HttpStatusCode.BadRequest, "INVALID_REQUEST", "The 'bodyHtmlTemplate' field is required.", null);
        }

        if (string.IsNullOrWhiteSpace(deduplicationKeyTemplate))
        {
            return CreateErrorResponse(HttpStatusCode.BadRequest, "INVALID_REQUEST", "The 'deduplicationKeyTemplate' field is required.", null);
        }

        if (variableDefinition == null)
        {
            return CreateErrorResponse(HttpStatusCode.BadRequest, "INVALID_REQUEST", "The 'variableDefinition' object is required.", null);
        }

        if (context == null)
        {
            return CreateErrorResponse(HttpStatusCode.BadRequest, "INVALID_REQUEST", "The 'context' object is required.", null);
        }

        List<string> requiredVariables;
        List<string> optionalVariables;

        if (!TryReadStringList(variableDefinition["required"], true, out requiredVariables, out string requiredError))
        {
            return CreateErrorResponse(HttpStatusCode.BadRequest, "INVALID_REQUEST", requiredError, null);
        }

        if (!TryReadStringList(variableDefinition["optional"], false, out optionalVariables, out string optionalError))
        {
            return CreateErrorResponse(HttpStatusCode.BadRequest, "INVALID_REQUEST", optionalError, null);
        }

        var declaredVariables = new HashSet<string>(StringComparer.Ordinal);
        var missingRequiredVariables = new List<string>();

        foreach (string variableName in requiredVariables)
        {
            if (declaredVariables.Add(variableName) && !HasContextValue(context, variableName))
            {
                missingRequiredVariables.Add(variableName);
            }
        }

        foreach (string variableName in optionalVariables)
        {
            declaredVariables.Add(variableName);
        }

        var unresolvedPlaceholders = new JArray();
        var unresolvedKeys = new HashSet<string>(StringComparer.Ordinal);

        string subject = RenderTemplate(subjectTemplate, "subjectTemplate", context, declaredVariables, unresolvedKeys, unresolvedPlaceholders);
        string bodyHtml = RenderTemplate(bodyHtmlTemplate, "bodyHtmlTemplate", context, declaredVariables, unresolvedKeys, unresolvedPlaceholders);
        string deduplicationKey = RenderTemplate(deduplicationKeyTemplate, "deduplicationKeyTemplate", context, declaredVariables, unresolvedKeys, unresolvedPlaceholders);

        if (missingRequiredVariables.Count > 0 || unresolvedPlaceholders.Count > 0)
        {
            var details = new JObject
            {
                ["missingRequiredVariables"] = new JArray(missingRequiredVariables),
                ["unresolvedPlaceholders"] = unresolvedPlaceholders
            };

            return CreateErrorResponse(
                HttpStatusCode.BadRequest,
                "TEMPLATE_VALIDATION_FAILED",
                "Template validation failed.",
                details
            );
        }

        var responseBody = new JObject
        {
            ["subject"] = subject,
            ["bodyHtml"] = bodyHtml,
            ["deduplicationKey"] = deduplicationKey
        };

        return CreateJsonResponse(HttpStatusCode.OK, responseBody);
    }

    private static bool TryReadStringList(JToken token, bool required, out List<string> values, out string error)
    {
        values = new List<string>();
        error = null;

        if (token == null || token.Type == JTokenType.Null)
        {
            if (required)
            {
                error = "The 'variableDefinition.required' array is required.";
                return false;
            }

            return true;
        }

        if (!(token is JArray items))
        {
            error = required
                ? "The 'variableDefinition.required' field must be an array of strings."
                : "The 'variableDefinition.optional' field must be an array of strings.";
            return false;
        }

        foreach (JToken item in items)
        {
            if (item == null || item.Type != JTokenType.String)
            {
                error = required
                    ? "The 'variableDefinition.required' field must contain only non-empty strings."
                    : "The 'variableDefinition.optional' field must contain only non-empty strings.";
                return false;
            }

            string value = item.ToString();
            if (string.IsNullOrWhiteSpace(value))
            {
                error = required
                    ? "The 'variableDefinition.required' field must contain only non-empty strings."
                    : "The 'variableDefinition.optional' field must contain only non-empty strings.";
                return false;
            }

            values.Add(value.Trim());
        }

        return true;
    }

    private static bool HasContextValue(JObject context, string variableName)
    {
        if (context == null || string.IsNullOrWhiteSpace(variableName))
        {
            return false;
        }

        if (!context.TryGetValue(variableName, out JToken value))
        {
            return false;
        }

        return value != null && value.Type != JTokenType.Null;
    }

    private static string RenderTemplate(
        string template,
        string templateField,
        JObject context,
        HashSet<string> declaredVariables,
        HashSet<string> unresolvedKeys,
        JArray unresolvedPlaceholders)
    {
        return PlaceholderRegex.Replace(template, match =>
        {
            string variableName = match.Groups["name"].Value;

            if (!HasContextValue(context, variableName))
            {
                string reason = declaredVariables.Contains(variableName)
                    ? "Variable is missing from the context object."
                    : "Variable is not declared in variableDefinition.";

                AddUnresolvedPlaceholder(unresolvedKeys, unresolvedPlaceholders, templateField, variableName, reason);
                return match.Value;
            }

            return ConvertTokenToString(context[variableName]);
        });
    }

    private static void AddUnresolvedPlaceholder(
        HashSet<string> unresolvedKeys,
        JArray unresolvedPlaceholders,
        string templateField,
        string placeholder,
        string reason)
    {
        string key = templateField + "|" + placeholder;
        if (!unresolvedKeys.Add(key))
        {
            return;
        }

        unresolvedPlaceholders.Add(new JObject
        {
            ["templateField"] = templateField,
            ["placeholder"] = placeholder,
            ["reason"] = reason
        });
    }

    private static string ConvertTokenToString(JToken token)
    {
        if (token == null || token.Type == JTokenType.Null)
        {
            return string.Empty;
        }

        if (token.Type == JTokenType.String)
        {
            return token.ToString();
        }

        return token.ToString(Formatting.None);
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

    private static HttpResponseMessage CreateJsonResponse(HttpStatusCode statusCode, JObject body)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = CreateJsonContent(body.ToString())
        };
    }

    private static HttpResponseMessage CreateErrorResponse(
        HttpStatusCode statusCode,
        string code,
        string message,
        JObject details)
    {
        var error = new JObject
        {
            ["error"] = new JObject
            {
                ["code"] = code,
                ["message"] = message
            }
        };

        if (details != null)
        {
            ((JObject)error["error"])["details"] = details;
        }

        return CreateJsonResponse(statusCode, error);
    }
}
