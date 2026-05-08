using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class Script : ScriptBase
{
    private const string BaseApiUrl = "https://nihbservices.express-scripts.ca";
    private const string SiteOrigin = "https://nihb-ssna.express-scripts.ca";
    private const string SiteReferer = "https://nihb-ssna.express-scripts.ca/";
    private const string DefaultDocCategory = "PHARMACY";
    private const string DefaultDocType = "BGLPF-EN";
    private const string DefaultDocSearchType = "BENEFITGRIDBEQ";
    private const string DefaultSearchLanguage = "E";
    private const int DefaultMaxRuntimeSeconds = 100;
    private const int DefaultParallelism = 8;

    public override async Task<HttpResponseMessage> ExecuteAsync()
    {
        string operationId = DecodeOperationId(this.Context.OperationId);

        return operationId switch
        {
            "ListDrugBenefitListCsvDocuments" => await HandleListDrugBenefitListCsvDocumentsAsync(),
            "DownloadDrugBenefitListCsv" => await HandleDownloadDrugBenefitListCsvAsync(),
            "SearchDrugBenefits" => await HandleSearchDrugBenefitsAsync(),
            "HydrateDrugBenefitList" => await HandleHydrateDrugBenefitListAsync(),
            _ => CreateErrorResponse(
                HttpStatusCode.BadRequest,
                $"Unknown operation: {operationId}",
                "UNKNOWN_OPERATION"
            )
        };
    }

    private async Task<HttpResponseMessage> HandleListDrugBenefitListCsvDocumentsAsync()
    {
        try
        {
            List<DocumentInfo> documents = await GetVisibleCsvDocumentsAsync(this.CancellationToken)
                .ConfigureAwait(false);

            var responseBody = new JObject
            {
                ["count"] = documents.Count,
                ["documents"] = new JArray(documents.Select(document => document.ToJson())),
                ["note"] = "The public NIHB search endpoint currently exposes the latest visible CSV document. If older CSV versions become visible through the same endpoint, they will also appear here."
            };

            return CreateJsonResponse(HttpStatusCode.OK, responseBody);
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(
                HttpStatusCode.InternalServerError,
                $"Failed to list visible NIHB CSV documents: {ex.Message}",
                "CSV_DOCUMENT_LIST_FAILED"
            );
        }
    }

    private async Task<HttpResponseMessage> HandleDownloadDrugBenefitListCsvAsync()
    {
        try
        {
            var query = HttpUtility.ParseQueryString(this.Context.Request.RequestUri.Query);
            string responseFormat = NormalizeResponseFormat(query["responseFormat"]);
            string requestedDocumentId = query["documentId"] ?? string.Empty;

            DocumentInfo document = await ResolveRequestedDocumentAsync(
                    requestedDocumentId,
                    this.CancellationToken)
                .ConfigureAwait(false);
            string contentBase64 = await DownloadDocumentBase64Async(
                document.DocumentId,
                this.CancellationToken)
                .ConfigureAwait(false);
            string csvText = DecodeBase64Text(contentBase64);
            List<string> itemNumbers = ExtractDistinctItemNumbers(csvText);

            var responseBody = new JObject
            {
                ["document"] = document.ToJson(),
                ["downloadedAt"] = DateTime.UtcNow.ToString("o"),
                ["contentType"] = "text/csv; charset=utf-8",
                ["responseFormat"] = responseFormat,
                ["lineCount"] = CountNonEmptyLines(csvText),
                ["dinCount"] = itemNumbers.Count
            };

            if (string.Equals(responseFormat, "Text", StringComparison.OrdinalIgnoreCase))
            {
                responseBody["contentText"] = csvText;
            }
            else
            {
                responseBody["contentBase64"] = contentBase64;
            }

            return CreateJsonResponse(HttpStatusCode.OK, responseBody);
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(
                HttpStatusCode.InternalServerError,
                $"Failed to download the NIHB Drug Benefit List CSV: {ex.Message}",
                "CSV_DOWNLOAD_FAILED"
            );
        }
    }

    private async Task<HttpResponseMessage> HandleSearchDrugBenefitsAsync()
    {
        try
        {
            JObject requestBody = await ParseRequestBodyAsync(required: true).ConfigureAwait(false);
            if (requestBody == null)
            {
                return CreateErrorResponse(
                    HttpStatusCode.BadRequest,
                    "A JSON request body is required.",
                    "MISSING_REQUEST_BODY"
                );
            }

            string language = NormalizeLanguage(GetStringValue(requestBody, "language"));
            JObject upstreamBody = BuildSearchBody(requestBody, language);

            if (!HasAtLeastOneSearchCriterion(upstreamBody))
            {
                return CreateErrorResponse(
                    HttpStatusCode.BadRequest,
                    "Provide at least one search criterion such as itemNumber or itemName.",
                    "MISSING_SEARCH_CRITERIA"
                );
            }

            JObject upstreamResponse = await PostJsonAsync(
                "/elasticclient/api/v1/onlineDrugBenefit/getOnlineDrugBenefitList",
                upstreamBody,
                GetServiceLanguage(language),
                this.CancellationToken)
                .ConfigureAwait(false);

            JArray items = GetArrayProperty(upstreamResponse, "data") ?? new JArray();
            var responseBody = new JObject
            {
                ["status"] = GetBooleanValue(upstreamResponse, "status"),
                ["errorCode"] = GetStringValue(upstreamResponse, "errorCode"),
                ["exceptionMessage"] = GetStringValue(upstreamResponse, "exceptionMessage"),
                ["count"] = items.Count,
                ["items"] = items
            };

            return CreateJsonResponse(HttpStatusCode.OK, responseBody);
        }
        catch (InvalidOperationException ex)
        {
            return CreateErrorResponse(
                HttpStatusCode.BadRequest,
                ex.Message,
                "INVALID_REQUEST_BODY"
            );
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(
                HttpStatusCode.InternalServerError,
                $"Failed to search NIHB drug benefits: {ex.Message}",
                "ITEM_SEARCH_FAILED"
            );
        }
    }

    private async Task<HttpResponseMessage> HandleHydrateDrugBenefitListAsync()
    {
        try
        {
            JObject requestBody = await ParseRequestBodyAsync(required: false).ConfigureAwait(false);
            requestBody = requestBody ?? new JObject();

            string language = NormalizeLanguage(GetStringValue(requestBody, "language"));
            int maxRuntimeSeconds = NormalizeMaxRuntimeSeconds(GetIntValue(requestBody, "maxRuntimeSeconds"));
            List<string> itemNumbers = NormalizeItemNumberList(GetPropertyValue(requestBody, "remainingItemNumbers"));
            bool startedFromCsv = itemNumbers.Count == 0;
            DocumentInfo document = null;

            if (startedFromCsv)
            {
                document = await GetLatestCsvDocumentAsync(this.CancellationToken)
                    .ConfigureAwait(false);
                string contentBase64 = await DownloadDocumentBase64Async(
                    document.DocumentId,
                    this.CancellationToken)
                    .ConfigureAwait(false);
                string csvText = DecodeBase64Text(contentBase64);
                itemNumbers = ExtractDistinctItemNumbers(csvText);
            }

            if (itemNumbers.Count == 0)
            {
                var emptyBody = new JObject
                {
                    ["startedFromCsv"] = startedFromCsv,
                    ["document"] = document == null ? null : document.ToJson(),
                    ["maxRuntimeSeconds"] = maxRuntimeSeconds,
                    ["timedOut"] = false,
                    ["totalItemNumbers"] = 0,
                    ["completedItemNumberCount"] = 0,
                    ["fetchedRecordCount"] = 0,
                    ["remainingItemNumberCount"] = 0,
                    ["remainingItemNumbers"] = new JArray(),
                    ["records"] = new JArray()
                };

                return CreateJsonResponse(HttpStatusCode.OK, emptyBody);
            }

            HydrationResult result = await HydrateItemNumbersAsync(
                itemNumbers,
                language,
                maxRuntimeSeconds)
                .ConfigureAwait(false);

            var responseBody = new JObject
            {
                ["startedFromCsv"] = startedFromCsv,
                ["document"] = document == null ? null : document.ToJson(),
                ["maxRuntimeSeconds"] = maxRuntimeSeconds,
                ["timedOut"] = result.TimedOut,
                ["totalItemNumbers"] = itemNumbers.Count,
                ["completedItemNumberCount"] = result.CompletedItemNumberCount,
                ["fetchedRecordCount"] = result.Records.Count,
                ["remainingItemNumberCount"] = result.RemainingItemNumbers.Count,
                ["remainingItemNumbers"] = new JArray(result.RemainingItemNumbers),
                ["records"] = new JArray(result.Records)
            };

            return CreateJsonResponse(HttpStatusCode.OK, responseBody);
        }
        catch (InvalidOperationException ex)
        {
            return CreateErrorResponse(
                HttpStatusCode.BadRequest,
                ex.Message,
                "INVALID_REQUEST_BODY"
            );
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(
                HttpStatusCode.InternalServerError,
                $"Failed to hydrate the NIHB Drug Benefit List: {ex.Message}",
                "HYDRATE_FAILED"
            );
        }
    }

    private async Task<HydrationResult> HydrateItemNumbersAsync(
        List<string> itemNumbers,
        string language,
        int maxRuntimeSeconds)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(this.CancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(maxRuntimeSeconds));

        var result = new HydrationResult();
        var succeeded = new bool[itemNumbers.Count];
        int nextIndex = 0;

        while (nextIndex < itemNumbers.Count)
        {
            if (timeoutCts.IsCancellationRequested)
            {
                result.TimedOut = true;
                break;
            }

            var batch = new List<PendingItemRequest>();
            while (nextIndex < itemNumbers.Count && batch.Count < DefaultParallelism)
            {
                batch.Add(new PendingItemRequest
                {
                    Index = nextIndex,
                    ItemNumber = itemNumbers[nextIndex]
                });
                nextIndex++;
            }

            FetchResult[] batchResults = await Task.WhenAll(batch.Select(
                    item => FetchItemNumberSafeAsync(item.ItemNumber, language, timeoutCts.Token)))
                .ConfigureAwait(false);

            for (int i = 0; i < batch.Count; i++)
            {
                FetchResult fetchResult = batchResults[i];
                if (fetchResult.Canceled)
                {
                    result.TimedOut = true;
                    continue;
                }

                if (!fetchResult.Success)
                {
                    continue;
                }

                succeeded[batch[i].Index] = true;
                result.CompletedItemNumberCount++;

                foreach (JObject record in fetchResult.Records)
                {
                    result.Records.Add(record);
                }
            }
        }

        for (int i = 0; i < itemNumbers.Count; i++)
        {
            if (!succeeded[i])
            {
                result.RemainingItemNumbers.Add(itemNumbers[i]);
            }
        }

        if (result.RemainingItemNumbers.Count > 0 && !result.TimedOut && nextIndex < itemNumbers.Count)
        {
            result.TimedOut = true;
        }

        return result;
    }

    private async Task<FetchResult> FetchItemNumberSafeAsync(
        string itemNumber,
        string language,
        CancellationToken cancellationToken)
    {
        try
        {
            JObject requestBody = new JObject
            {
                ["itemNumber"] = itemNumber,
                ["itemName"] = string.Empty,
                ["strength"] = string.Empty,
                ["dosage"] = string.Empty,
                ["chemicalName"] = string.Empty,
                ["ahfsClass"] = string.Empty,
                ["manufacture"] = string.Empty,
                ["benefitCoverage"] = string.Empty,
                ["pharmacyItemReviewStatus"] = string.Empty,
                ["language"] = language
            };

            JObject upstreamResponse = await PostJsonAsync(
                "/elasticclient/api/v1/onlineDrugBenefit/getOnlineDrugBenefitList",
                requestBody,
                GetServiceLanguage(language),
                cancellationToken)
                .ConfigureAwait(false);

            if (!GetBooleanValue(upstreamResponse, "status"))
            {
                return FetchResult.Failure();
            }

            JArray records = GetArrayProperty(upstreamResponse, "data") ?? new JArray();
            var normalizedRecords = new List<JObject>();
            foreach (JToken token in records)
            {
                if (token is JObject record)
                {
                    normalizedRecords.Add(record);
                }
            }

            return FetchResult.SuccessResult(normalizedRecords);
        }
        catch (OperationCanceledException)
        {
            return FetchResult.CanceledResult();
        }
        catch
        {
            return FetchResult.Failure();
        }
    }

    private async Task<DocumentInfo> GetLatestCsvDocumentAsync(CancellationToken cancellationToken)
    {
        List<DocumentInfo> documents = await GetVisibleCsvDocumentsAsync(cancellationToken)
            .ConfigureAwait(false);
        if (documents.Count == 0)
        {
            throw new InvalidOperationException("NIHB did not return a CSV Drug Benefit List document.");
        }

        return documents[0];
    }

    private async Task<List<DocumentInfo>> GetVisibleCsvDocumentsAsync(CancellationToken cancellationToken)
    {
        var requestBody = new JObject
        {
            ["docCat"] = DefaultDocCategory,
            ["language"] = string.Empty,
            ["docType"] = DefaultDocType,
            ["itemCode"] = string.Empty,
            ["type"] = DefaultDocSearchType
        };

        JObject responseBody = await PostJsonAsync(
            "/docManagement/docManagement/searchPublicDoc",
            requestBody,
            "en",
            cancellationToken)
            .ConfigureAwait(false);

        JArray documents = GetArrayProperty(responseBody, "data");
        if (documents == null || documents.Count == 0)
        {
            throw new InvalidOperationException("NIHB did not return any public documents.");
        }

        List<DocumentInfo> csvDocuments = documents
            .OfType<JObject>()
            .Where(document =>
            {
                string fileName = GetStringValue(document, "fileName");
                return fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) &&
                    fileName.IndexOf("drug benefit list", StringComparison.OrdinalIgnoreCase) >= 0;
            })
            .OrderByDescending(document => GetIntValue(document, "effectiveDate") ?? 0)
            .ThenByDescending(document => GetStringValue(document, "updatedDate"))
            .Select(document => CreateDocumentInfo(document))
            .ToList();

        if (csvDocuments.Count == 0)
        {
            throw new InvalidOperationException("NIHB did not return a CSV Drug Benefit List document.");
        }

        return csvDocuments;
    }

    private async Task<DocumentInfo> ResolveRequestedDocumentAsync(
        string requestedDocumentId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestedDocumentId))
        {
            return await GetLatestCsvDocumentAsync(cancellationToken).ConfigureAwait(false);
        }

        List<DocumentInfo> documents = await GetVisibleCsvDocumentsAsync(cancellationToken)
            .ConfigureAwait(false);
        DocumentInfo requested = documents.FirstOrDefault(document =>
            string.Equals(document.DocumentId, requestedDocumentId, StringComparison.OrdinalIgnoreCase));
        if (requested == null)
        {
            throw new InvalidOperationException(
                "The requested documentId is not currently visible in the NIHB public document search response.");
        }

        return requested;
    }

    private static DocumentInfo CreateDocumentInfo(JObject csvDocument)
    {
        string documentId = GetStringValue(csvDocument, "documentId");
        if (string.IsNullOrWhiteSpace(documentId))
        {
            throw new InvalidOperationException("The selected NIHB document is missing its documentId.");
        }

        return new DocumentInfo
        {
            FileName = GetStringValue(csvDocument, "fileName"),
            DocumentId = documentId,
            EffectiveDate = (GetIntValue(csvDocument, "effectiveDate") ?? 0).ToString(),
            UpdatedDate = GetStringValue(csvDocument, "updatedDate"),
            Status = GetStringValue(csvDocument, "status"),
            Province = GetStringValue(csvDocument, "province")
        };
    }

    private async Task<string> DownloadDocumentBase64Async(
        string documentId,
        CancellationToken cancellationToken)
    {
        var requestBody = new JObject
        {
            ["documentId"] = documentId
        };

        JObject responseBody = await PostJsonAsync(
            "/docManagement/docManagement/doc",
            requestBody,
            "en",
            cancellationToken)
            .ConfigureAwait(false);

        JArray data = GetArrayProperty(responseBody, "data");
        string contentBase64 = data != null && data.Count > 0 ? data[0]?.ToString() : null;
        if (string.IsNullOrWhiteSpace(contentBase64))
        {
            throw new InvalidOperationException("NIHB did not return any downloadable document content.");
        }

        return contentBase64;
    }

    private async Task<JObject> PostJsonAsync(
        string relativePath,
        JObject body,
        string serviceLanguage,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(BaseApiUrl + relativePath));

        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        request.Headers.TryAddWithoutValidation("Origin", SiteOrigin);
        request.Headers.TryAddWithoutValidation("Referer", SiteReferer);
        request.Headers.TryAddWithoutValidation("X-Service-Language", serviceLanguage);
        request.Content = CreateJsonContent(body.ToString(Newtonsoft.Json.Formatting.None));

        HttpResponseMessage response = await this.Context.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        string content = response.Content == null
            ? string.Empty
            : await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"NIHB returned {(int)response.StatusCode} {response.ReasonPhrase}. {content}");
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return new JObject();
        }

        try
        {
            return JObject.Parse(content);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"NIHB returned invalid JSON: {ex.Message}");
        }
    }

    private JObject BuildSearchBody(JObject requestBody, string language)
    {
        return new JObject
        {
            ["itemNumber"] = NormalizeItemNumber(GetStringValue(requestBody, "itemNumber")),
            ["itemName"] = GetStringValue(requestBody, "itemName"),
            ["strength"] = GetStringValue(requestBody, "strength"),
            ["dosage"] = GetStringValue(requestBody, "dosage"),
            ["chemicalName"] = GetStringValue(requestBody, "chemicalName"),
            ["ahfsClass"] = GetStringValue(requestBody, "ahfsClass"),
            ["manufacture"] = GetStringValue(requestBody, "manufacture"),
            ["benefitCoverage"] = GetStringValue(requestBody, "benefitCoverage"),
            ["pharmacyItemReviewStatus"] = GetStringValue(requestBody, "pharmacyItemReviewStatus"),
            ["language"] = language
        };
    }

    private bool HasAtLeastOneSearchCriterion(JObject requestBody)
    {
        string[] keys = new[]
        {
            "itemNumber",
            "itemName",
            "strength",
            "dosage",
            "chemicalName",
            "ahfsClass",
            "manufacture",
            "benefitCoverage",
            "pharmacyItemReviewStatus"
        };

        return keys.Any(key => !string.IsNullOrWhiteSpace(GetStringValue(requestBody, key)));
    }

    private async Task<JObject> ParseRequestBodyAsync(bool required)
    {
        if (this.Context.Request.Content == null)
        {
            return required ? null : new JObject();
        }

        string content = await this.Context.Request.Content.ReadAsStringAsync()
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(content))
        {
            return required ? null : new JObject();
        }

        try
        {
            return JObject.Parse(content);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid JSON in request body: {ex.Message}");
        }
    }

    private static string DecodeBase64Text(string contentBase64)
    {
        byte[] bytes = Convert.FromBase64String(contentBase64);
        return Encoding.UTF8.GetString(bytes);
    }

    private static int CountNonEmptyLines(string text)
    {
        return SplitLines(text).Count(line => !string.IsNullOrWhiteSpace(line));
    }

    private static List<string> ExtractDistinctItemNumbers(string csvText)
    {
        string[] lines = SplitLines(csvText).ToArray();
        int headerIndex = -1;
        int itemNumberColumnIndex = -1;

        for (int i = 0; i < lines.Length; i++)
        {
            List<string> fields = ParseCsvLine(lines[i]);
            itemNumberColumnIndex = fields.FindIndex(field =>
                string.Equals(field.Trim(), "DIN", StringComparison.OrdinalIgnoreCase));

            if (itemNumberColumnIndex >= 0)
            {
                headerIndex = i;
                break;
            }
        }

        if (headerIndex < 0 || itemNumberColumnIndex < 0)
        {
            throw new InvalidOperationException("The NIHB CSV does not contain a DIN column.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var itemNumbers = new List<string>();

        for (int i = headerIndex + 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            List<string> fields = ParseCsvLine(lines[i]);
            if (fields.Count <= itemNumberColumnIndex)
            {
                continue;
            }

            string itemNumber = NormalizeItemNumber(fields[itemNumberColumnIndex]);
            if (string.IsNullOrWhiteSpace(itemNumber))
            {
                continue;
            }

            if (seen.Add(itemNumber))
            {
                itemNumbers.Add(itemNumber);
            }
        }

        return itemNumbers;
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        return text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char character = line[i];
            if (character == '"')
            {
                bool escapedQuote = inQuotes && i + 1 < line.Length && line[i + 1] == '"';
                if (escapedQuote)
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (character == ',' && !inQuotes)
            {
                fields.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(character);
        }

        fields.Add(current.ToString());
        return fields;
    }

    private static List<string> NormalizeItemNumberList(JToken token)
    {
        var itemNumbers = new List<string>();
        if (token == null || token.Type == JTokenType.Null)
        {
            return itemNumbers;
        }

        IEnumerable<string> rawValues;
        if (token is JArray array)
        {
            rawValues = array.Select(value => value?.ToString());
        }
        else
        {
            rawValues = token
                .ToString()
                .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim());
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawValue in rawValues)
        {
            string itemNumber = NormalizeItemNumber(rawValue);
            if (string.IsNullOrWhiteSpace(itemNumber))
            {
                continue;
            }

            if (seen.Add(itemNumber))
            {
                itemNumbers.Add(itemNumber);
            }
        }

        return itemNumbers;
    }

    private static string NormalizeItemNumber(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string trimmed = value.Trim();
        if (trimmed.All(char.IsDigit) && trimmed.Length < 8)
        {
            trimmed = trimmed.PadLeft(8, '0');
        }

        return trimmed;
    }

    private static string NormalizeLanguage(string language)
    {
        return string.Equals(language, "F", StringComparison.OrdinalIgnoreCase)
            ? "F"
            : DefaultSearchLanguage;
    }

    private static string GetServiceLanguage(string language)
    {
        return string.Equals(language, "F", StringComparison.OrdinalIgnoreCase)
            ? "fr"
            : "en";
    }

    private static int NormalizeMaxRuntimeSeconds(int? value)
    {
        if (!value.HasValue || value.Value <= 0)
        {
            return DefaultMaxRuntimeSeconds;
        }

        return Math.Min(value.Value, DefaultMaxRuntimeSeconds);
    }

    private static string NormalizeResponseFormat(string value)
    {
        return string.Equals(value, "Text", StringComparison.OrdinalIgnoreCase)
            ? "Text"
            : "Base64";
    }

    private static string GetStringValue(JObject obj, string propertyName)
    {
        JToken token = GetPropertyValue(obj, propertyName);
        return token == null || token.Type == JTokenType.Null ? string.Empty : token.ToString();
    }

    private static int? GetIntValue(JObject obj, string propertyName)
    {
        JToken token = GetPropertyValue(obj, propertyName);
        if (token == null || token.Type == JTokenType.Null)
        {
            return null;
        }

        return token.Type == JTokenType.Integer
            ? token.Value<int>()
            : int.TryParse(token.ToString(), out int parsed)
                ? parsed
                : (int?)null;
    }

    private static bool GetBooleanValue(JObject obj, string propertyName)
    {
        JToken token = GetPropertyValue(obj, propertyName);
        if (token == null || token.Type == JTokenType.Null)
        {
            return false;
        }

        return token.Type == JTokenType.Boolean
            ? token.Value<bool>()
            : bool.TryParse(token.ToString(), out bool parsed) && parsed;
    }

    private static JArray GetArrayProperty(JObject obj, string propertyName)
    {
        return GetPropertyValue(obj, propertyName) as JArray;
    }

    private static JToken GetPropertyValue(JObject obj, string propertyName)
    {
        if (obj == null)
        {
            return null;
        }

        obj.TryGetValue(propertyName, StringComparison.OrdinalIgnoreCase, out JToken value);
        return value;
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

    private HttpResponseMessage CreateJsonResponse(HttpStatusCode statusCode, JObject body)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = CreateJsonContent(body.ToString())
        };
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
                ["message"] = message,
                ["code"] = code
            }
        };

        return CreateJsonResponse(statusCode, errorBody);
    }

    private sealed class DocumentInfo
    {
        public string FileName { get; set; }

        public string DocumentId { get; set; }

        public string EffectiveDate { get; set; }

        public string UpdatedDate { get; set; }

        public string Status { get; set; }

        public string Province { get; set; }

        public JObject ToJson()
        {
            return new JObject
            {
                ["fileName"] = this.FileName,
                ["documentId"] = this.DocumentId,
                ["effectiveDate"] = this.EffectiveDate,
                ["updatedDate"] = this.UpdatedDate,
                ["status"] = this.Status,
                ["province"] = this.Province
            };
        }
    }

    private sealed class PendingItemRequest
    {
        public int Index { get; set; }

        public string ItemNumber { get; set; }
    }

    private sealed class HydrationResult
    {
        public HydrationResult()
        {
            this.Records = new List<JObject>();
            this.RemainingItemNumbers = new List<string>();
        }

        public bool TimedOut { get; set; }

        public int CompletedItemNumberCount { get; set; }

        public List<JObject> Records { get; }

        public List<string> RemainingItemNumbers { get; }
    }

    private sealed class FetchResult
    {
        private FetchResult(bool success, bool canceled, List<JObject> records)
        {
            this.Success = success;
            this.Canceled = canceled;
            this.Records = records ?? new List<JObject>();
        }

        public bool Success { get; }

        public bool Canceled { get; }

        public List<JObject> Records { get; }

        public static FetchResult SuccessResult(List<JObject> records)
        {
            return new FetchResult(true, false, records);
        }

        public static FetchResult Failure()
        {
            return new FetchResult(false, false, null);
        }

        public static FetchResult CanceledResult()
        {
            return new FetchResult(false, true, null);
        }
    }
}