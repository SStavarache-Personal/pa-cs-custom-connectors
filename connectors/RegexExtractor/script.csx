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

public class Script : ScriptBase
{
    private const int MaxInputLength = 500_000;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(10);

    public override async Task<HttpResponseMessage> ExecuteAsync()
    {
        string operationId = DecodeOperationId(this.Context.OperationId);

        return operationId switch
        {
            "ExtractMatches" => await HandleExtractMatchesAsync(),
            "TestPattern" => await HandleTestPatternAsync(),
            "ReplaceMatches" => await HandleReplaceMatchesAsync(),
            "ExtractFrenchDates" => await HandleExtractFrenchDatesAsync(),
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

    private async Task<HttpResponseMessage> HandleExtractMatchesAsync()
    {
        var (text, pattern, options, error) = await ParseRegexRequestAsync("text", "pattern");
        if (error != null) return error;

        Regex regex;
        try
        {
            regex = new Regex(pattern, options, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            return CreateErrorResponse(
                HttpStatusCode.BadRequest,
                $"Invalid regex pattern: {ex.Message}",
                "INVALID_PATTERN"
            );
        }

        MatchCollection matchCollection;
        try
        {
            matchCollection = regex.Matches(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return CreateErrorResponse(
                HttpStatusCode.BadRequest,
                "Regex evaluation timed out. Simplify the pattern or reduce input size.",
                "REGEX_TIMEOUT"
            );
        }

        var matches = new JArray();
        foreach (Match m in matchCollection)
        {
            var groups = new JObject();
            for (int i = 0; i < m.Groups.Count; i++)
            {
                string groupName = regex.GroupNameFromNumber(i) ?? i.ToString();
                groups[groupName] = m.Groups[i].Value;
            }

            matches.Add(new JObject
            {
                ["value"] = m.Value,
                ["index"] = m.Index,
                ["groups"] = groups
            });
        }

        var responseBody = new JObject
        {
            ["matchCount"] = matches.Count,
            ["matches"] = matches
        };

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = CreateJsonContent(responseBody.ToString())
        };
    }

    private async Task<HttpResponseMessage> HandleTestPatternAsync()
    {
        var (text, pattern, options, error) = await ParseRegexRequestAsync("text", "pattern");
        if (error != null) return error;

        Regex regex;
        try
        {
            regex = new Regex(pattern, options, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            return CreateErrorResponse(
                HttpStatusCode.BadRequest,
                $"Invalid regex pattern: {ex.Message}",
                "INVALID_PATTERN"
            );
        }

        Match match;
        try
        {
            match = regex.Match(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return CreateErrorResponse(
                HttpStatusCode.BadRequest,
                "Regex evaluation timed out. Simplify the pattern or reduce input size.",
                "REGEX_TIMEOUT"
            );
        }

        var responseBody = new JObject
        {
            ["isMatch"] = match.Success,
            ["matchedValue"] = match.Success ? match.Value : null
        };

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = CreateJsonContent(responseBody.ToString())
        };
    }

    private async Task<HttpResponseMessage> HandleReplaceMatchesAsync()
    {
        var (text, pattern, options, error) = await ParseRegexRequestAsync("text", "pattern");
        if (error != null) return error;

        string content = await this.Context.Request.Content
            .ReadAsStringAsync()
            .ConfigureAwait(false);
        JObject body = JObject.Parse(content);

        string replacement = body["replacement"]?.ToString();
        if (replacement == null)
        {
            return CreateErrorResponse(
                HttpStatusCode.BadRequest,
                "The 'replacement' field is required.",
                "MISSING_REQUIRED_FIELD"
            );
        }

        Regex regex;
        try
        {
            regex = new Regex(pattern, options, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            return CreateErrorResponse(
                HttpStatusCode.BadRequest,
                $"Invalid regex pattern: {ex.Message}",
                "INVALID_PATTERN"
            );
        }

        string result;
        int replacementCount;
        try
        {
            replacementCount = regex.Matches(text).Count;
            result = regex.Replace(text, replacement);
        }
        catch (RegexMatchTimeoutException)
        {
            return CreateErrorResponse(
                HttpStatusCode.BadRequest,
                "Regex evaluation timed out. Simplify the pattern or reduce input size.",
                "REGEX_TIMEOUT"
            );
        }

        var responseBody = new JObject
        {
            ["result"] = result,
            ["replacementCount"] = replacementCount
        };

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = CreateJsonContent(responseBody.ToString())
        };
    }

    private async Task<HttpResponseMessage> HandleExtractFrenchDatesAsync()
    {
        string content = await this.Context.Request.Content
            .ReadAsStringAsync()
            .ConfigureAwait(false);

        JObject body;
        try
        {
            body = JObject.Parse(content);
        }
        catch (JsonException)
        {
            return CreateErrorResponse(HttpStatusCode.BadRequest, "Invalid JSON in request body.", "INVALID_JSON");
        }

        string text = body["text"]?.ToString();
        if (string.IsNullOrEmpty(text))
        {
            return CreateErrorResponse(HttpStatusCode.BadRequest, "The 'text' field is required.", "MISSING_REQUIRED_FIELD");
        }

        if (text.Length > MaxInputLength)
        {
            return CreateErrorResponse(HttpStatusCode.BadRequest,
                $"Input text exceeds maximum length of {MaxInputLength} characters.", "INPUT_TOO_LARGE");
        }

        // French month names mapped to numeric values
        var monthMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["janvier"] = "01", ["f\u00e9vrier"] = "02", ["fevrier"] = "02",
            ["mars"] = "03", ["avril"] = "04", ["mai"] = "05",
            ["juin"] = "06", ["juillet"] = "07", ["ao\u00fbt"] = "08", ["aout"] = "08",
            ["septembre"] = "09", ["octobre"] = "10", ["novembre"] = "11", ["d\u00e9cembre"] = "12", ["decembre"] = "12"
        };

        string monthAlternation = string.Join("|", monthMap.Keys);

        // Pattern: optional leading text, day (with optional ordinal "er"), French month name, 4-digit year
        string pattern = @"(?<day>[0-3]?\d)\s*(?:er)?\s+(?<month>" + monthAlternation + @")\s+(?<year>\d{4})";

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.IgnoreCase, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            return CreateErrorResponse(HttpStatusCode.BadRequest,
                $"Internal pattern error: {ex.Message}", "INVALID_PATTERN");
        }

        MatchCollection matchCollection;
        try
        {
            matchCollection = regex.Matches(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return CreateErrorResponse(HttpStatusCode.BadRequest,
                "Regex evaluation timed out. Reduce input size.", "REGEX_TIMEOUT");
        }

        var dates = new JArray();
        foreach (Match m in matchCollection)
        {
            string dayStr = m.Groups["day"].Value;
            string monthName = m.Groups["month"].Value;
            string yearStr = m.Groups["year"].Value;

            string monthNum = monthMap.ContainsKey(monthName) ? monthMap[monthName] : "00";
            string dayPadded = dayStr.PadLeft(2, '0');

            dates.Add(new JObject
            {
                ["original"] = m.Value,
                ["day"] = int.Parse(dayStr),
                ["month"] = monthName.ToLower(),
                ["year"] = int.Parse(yearStr),
                ["iso"] = $"{yearStr}-{monthNum}-{dayPadded}",
                ["index"] = m.Index
            });
        }

        var responseBody = new JObject
        {
            ["dateCount"] = dates.Count,
            ["dates"] = dates
        };

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = CreateJsonContent(responseBody.ToString())
        };
    }

    // ========================================================================
    // HELPER METHODS
    // ========================================================================

    private async Task<(string text, string pattern, RegexOptions options, HttpResponseMessage error)> ParseRegexRequestAsync(
        string textField, string patternField)
    {
        string content = await this.Context.Request.Content
            .ReadAsStringAsync()
            .ConfigureAwait(false);

        JObject body;
        try
        {
            body = JObject.Parse(content);
        }
        catch (JsonException)
        {
            return (null, null, RegexOptions.None,
                CreateErrorResponse(HttpStatusCode.BadRequest, "Invalid JSON in request body.", "INVALID_JSON"));
        }

        string text = body[textField]?.ToString();
        if (string.IsNullOrEmpty(text))
        {
            return (null, null, RegexOptions.None,
                CreateErrorResponse(HttpStatusCode.BadRequest, $"The '{textField}' field is required.", "MISSING_REQUIRED_FIELD"));
        }

        if (text.Length > MaxInputLength)
        {
            return (null, null, RegexOptions.None,
                CreateErrorResponse(HttpStatusCode.BadRequest,
                    $"Input text exceeds maximum length of {MaxInputLength} characters.", "INPUT_TOO_LARGE"));
        }

        string pattern = body[patternField]?.ToString();
        if (string.IsNullOrEmpty(pattern))
        {
            return (null, null, RegexOptions.None,
                CreateErrorResponse(HttpStatusCode.BadRequest, $"The '{patternField}' field is required.", "MISSING_REQUIRED_FIELD"));
        }

        RegexOptions options = RegexOptions.None;
        string optionsStr = body["options"]?.ToString();
        if (!string.IsNullOrWhiteSpace(optionsStr))
        {
            string[] parts = optionsStr.Split(',');
            foreach (string part in parts)
            {
                string trimmed = part.Trim();
                if (Enum.TryParse<RegexOptions>(trimmed, true, out RegexOptions parsed))
                {
                    options |= parsed;
                }
                else
                {
                    return (null, null, RegexOptions.None,
                        CreateErrorResponse(HttpStatusCode.BadRequest,
                            $"Unknown regex option: '{trimmed}'. Valid options: IgnoreCase, Multiline, Singleline, ExplicitCapture.",
                            "INVALID_OPTION"));
                }
            }
        }

        return (text, pattern, options, null);
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

    private HttpResponseMessage CreateErrorResponse(
        HttpStatusCode statusCode,
        string message,
        string code)
    {
        var errorBody = new JObject
        {
            ["error"] = new JObject
            {
                ["code"] = code,
                ["message"] = message
            }
        };

        return new HttpResponseMessage(statusCode)
        {
            Content = CreateJsonContent(errorBody.ToString())
        };
    }
}
