using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class Script : ScriptBase
{
    private const string BaseApiUrl = "https://health-products.canada.ca/api/natural-licences/";
    private const int SafetyWindowSeconds = 100;

    private static readonly EndpointConfig MedicinalIngredientConfig = new EndpointConfig(
        "medicinalingredient",
        true,
        false,
        new[]
        {
            "lnhpd_id",
            "ingredient_name",
            "ingredient_Text",
            "potency_amount",
            "potency_constituent",
            "potency_unit_of_measure",
            "quantity",
            "quantity_minimum",
            "quantity_maximum",
            "quantity_unit_of_measure",
            "ratio_numerator",
            "ratio_denominator",
            "dried_herb_equivalent",
            "dhe_unit_of_measure",
            "extract_type_desc",
            "source_material"
        }
    );

    private static readonly EndpointConfig NonMedicinalIngredientConfig = new EndpointConfig(
        "nonmedicinalingredient",
        false,
        true,
        new[]
        {
            "lnhpd_id",
            "ingredient_name"
        }
    );

    private static readonly EndpointConfig ProductDoseConfig = new EndpointConfig(
        "productdose",
        false,
        true,
        new[]
        {
            "lnhpd_id",
            "dose_id",
            "population_type_desc",
            "age",
            "age_minimum",
            "age_maximum",
            "uom_type_desc_age",
            "quantity_dose",
            "quantity_dose_minimum",
            "quantity_dose_maximum",
            "uom_type_desc_quantity_dose",
            "frequency",
            "frequency_minimum",
            "frequency_maximum",
            "uom_type_desc_frequency"
        }
    );

    private static readonly EndpointConfig ProductLicenceConfig = new EndpointConfig(
        "productlicence",
        false,
        true,
        new[]
        {
            "lnhpd_id",
            "licence_number",
            "licence_date",
            "revised_date",
            "time_receipt",
            "date_start",
            "product_name_id",
            "product_name",
            "dosage_form",
            "company_id",
            "company_name_id",
            "company_name",
            "sub_submission_type_code",
            "sub_submission_type_desc",
            "flag_primary_name",
            "flag_product_status",
            "flag_attested_monograph"
        }
    );

    private static readonly EndpointConfig ProductPurposeConfig = new EndpointConfig(
        "productpurpose",
        true,
        false,
        new[]
        {
            "text_id",
            "lnhpd_id",
            "purpose"
        }
    );

    private static readonly EndpointConfig ProductRiskConfig = new EndpointConfig(
        "productrisk",
        true,
        false,
        new[]
        {
            "lnhpd_id",
            "risk_id",
            "risk_type_desc",
            "sub_risk_type_desc",
            "risk_text"
        }
    );

    private static readonly EndpointConfig ProductRouteConfig = new EndpointConfig(
        "productroute",
        false,
        true,
        new[]
        {
            "lnhpd_id",
            "route_id",
            "route_type_desc"
        }
    );

    public override async Task<HttpResponseMessage> ExecuteAsync()
    {
        try
        {
            string operationId = DecodeOperationId(this.Context.OperationId);

            switch (operationId)
            {
                case "GetMedicinalIngredients":
                    return await HandleDirectEndpointAsync("medicinalingredient", MedicinalIngredientConfig).ConfigureAwait(false);
                case "GetNonMedicinalIngredients":
                    return await HandleDirectEndpointAsync("nonmedicinalingredient", NonMedicinalIngredientConfig).ConfigureAwait(false);
                case "GetProductDoses":
                    return await HandleDirectEndpointAsync("productdose", ProductDoseConfig).ConfigureAwait(false);
                case "GetProductLicences":
                    return await HandleDirectEndpointAsync("productlicence", ProductLicenceConfig).ConfigureAwait(false);
                case "GetProductPurposes":
                    return await HandleDirectEndpointAsync("productpurpose", ProductPurposeConfig).ConfigureAwait(false);
                case "GetProductRisks":
                    return await HandleDirectEndpointAsync("productrisk", ProductRiskConfig).ConfigureAwait(false);
                case "GetProductRoutes":
                    return await HandleDirectEndpointAsync("productroute", ProductRouteConfig).ConfigureAwait(false);
                case "BulkMedicinalIngredients":
                    return await HandleBulkPaginatedEndpointAsync("medicinalingredient", MedicinalIngredientConfig).ConfigureAwait(false);
                case "BulkProductPurposes":
                    return await HandleBulkPaginatedEndpointAsync("productpurpose", ProductPurposeConfig).ConfigureAwait(false);
                case "BulkProductRisks":
                    return await HandleBulkPaginatedEndpointAsync("productrisk", ProductRiskConfig).ConfigureAwait(false);
                case "BulkProductLicences":
                    return await HandleProductLicenceBulkAsync().ConfigureAwait(false);
                default:
                    return CreateErrorResponse(
                        HttpStatusCode.BadRequest,
                        "UNKNOWN_OPERATION",
                        "Unknown operation: " + operationId,
                        null
                    );
            }
        }
        catch (ConnectorException ex)
        {
            return CreateErrorResponse(ex.StatusCode, ex.Code, ex.Message, ex.Details);
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(
                HttpStatusCode.InternalServerError,
                "UNEXPECTED_ERROR",
                "Unexpected execution failure: " + ex.Message,
                null
            );
        }
    }

    private async Task<HttpResponseMessage> HandleDirectEndpointAsync(string endpointName, EndpointConfig config)
    {
        var query = HttpUtility.ParseQueryString(this.Context.Request.RequestUri.Query);
        string lang = NormalizeLanguage(query["lang"]);
        ResultFormat format = ParseResultFormat(query["outputFormat"]);
        string id = query["id"];
        int? page = ParseOptionalPositiveInt(query["page"], "page");

        if (config.RequiresId && string.IsNullOrWhiteSpace(id))
        {
            throw new ConnectorException(
                HttpStatusCode.BadRequest,
                "INVALID_REQUEST",
                "The 'id' query parameter is required for this action.",
                null
            );
        }

        if (!config.SupportsPage && page.HasValue)
        {
            throw new ConnectorException(
                HttpStatusCode.BadRequest,
                "INVALID_REQUEST",
                "The 'page' query parameter is not supported for this action.",
                null
            );
        }

        if (!string.IsNullOrWhiteSpace(id) && page.HasValue)
        {
            throw new ConnectorException(
                HttpStatusCode.BadRequest,
                "INVALID_REQUEST",
                "Use either 'id' or 'page', not both.",
                null
            );
        }

        string uri = BuildEndpointUri(config.Path, lang, id, page);
        JToken payload = await GetJsonTokenAsync(uri).ConfigureAwait(false);
        NormalizedPayload normalized = NormalizePayload(payload);

        JObject response = CreateEndpointPayload(endpointName, lang, format, normalized.Metadata, normalized.Data, config.CsvColumns);
        return CreateJsonResponse(HttpStatusCode.OK, response);
    }

    private async Task<HttpResponseMessage> HandleBulkPaginatedEndpointAsync(string endpointName, EndpointConfig config)
    {
        JObject body = await ReadBodyObjectAsync().ConfigureAwait(false);
        string lang = NormalizeLanguage(body["lang"]?.ToString());
        ResultFormat format = ParseResultFormat(body["outputFormat"]?.ToString());
        List<string> ids = ReadStringArray(body["ids"]);
        bool usingIds = ids.Count > 0;
        int startPage = body["startPage"]?.Value<int?>() ?? 1;
        int? endPage = body["endPage"]?.Value<int?>();

        if (startPage < 1)
        {
            throw new ConnectorException(HttpStatusCode.BadRequest, "INVALID_REQUEST", "The 'startPage' value must be 1 or greater.", null);
        }

        if (endPage.HasValue && endPage.Value < startPage)
        {
            throw new ConnectorException(HttpStatusCode.BadRequest, "INVALID_REQUEST", "The 'endPage' value must be greater than or equal to 'startPage'.", null);
        }

        if (usingIds && (body["startPage"] != null || body["endPage"] != null))
        {
            throw new ConnectorException(
                HttpStatusCode.BadRequest,
                "INVALID_REQUEST",
                "When 'ids' is supplied, omit 'startPage' and 'endPage'.",
                null
            );
        }

        JArray allItems = new JArray();
        JObject lastMetadata = null;
        JArray pagesFetched = new JArray();
        int? nextStartPage = null;
        bool completed = true;
        Stopwatch stopwatch = Stopwatch.StartNew();

        if (usingIds)
        {
            foreach (string id in ids)
            {
                string uri = BuildEndpointUri(config.Path, lang, id, null);
                NormalizedPayload normalized = NormalizePayload(await GetJsonTokenAsync(uri).ConfigureAwait(false));
                AppendItems(allItems, normalized.Data);
                lastMetadata = normalized.Metadata;
            }
        }
        else
        {
            int page = startPage;

            while (true)
            {
                string uri = BuildEndpointUri(config.Path, lang, null, page);
                NormalizedPayload normalized = NormalizePayload(await GetJsonTokenAsync(uri).ConfigureAwait(false));
                AppendItems(allItems, normalized.Data);
                lastMetadata = normalized.Metadata;
                pagesFetched.Add(page);

                bool hasNext = !string.IsNullOrWhiteSpace(normalized.NextPage);
                if (!hasNext)
                {
                    break;
                }

                if (endPage.HasValue && page >= endPage.Value)
                {
                    break;
                }

                page += 1;

                if (!endPage.HasValue && stopwatch.Elapsed.TotalSeconds >= SafetyWindowSeconds)
                {
                    nextStartPage = page;
                    completed = false;
                    break;
                }
            }
        }

        JObject response = CreateEndpointPayload(endpointName, lang, format, lastMetadata, allItems, config.CsvColumns);
        response["summary"] = new JObject
        {
            ["idsRequested"] = new JArray(ids),
            ["pagesFetched"] = pagesFetched,
            ["nextStartPage"] = nextStartPage == null ? JValue.CreateNull() : new JValue(nextStartPage.Value),
            ["completed"] = completed,
            ["elapsedMs"] = stopwatch.ElapsedMilliseconds
        };

        return CreateJsonResponse(HttpStatusCode.OK, response);
    }

    private async Task<HttpResponseMessage> HandleProductLicenceBulkAsync()
    {
        JObject body = await ReadBodyObjectAsync().ConfigureAwait(false);
        string lang = NormalizeLanguage(body["lang"]?.ToString());
        ResultFormat format = ParseResultFormat(body["outputFormat"]?.ToString(), ResultFormat.Csv);
        List<string> ids = ReadStringArray(body["ids"]);
        DateTime? revisedSince = ParseOptionalDate(body["ifRevisedSince"]?.ToString(), "ifRevisedSince");
        Stopwatch stopwatch = Stopwatch.StartNew();

        if (ids.Count > 0)
        {
            JArray allItems = new JArray();
            foreach (string id in ids)
            {
                string uri = BuildEndpointUri(ProductLicenceConfig.Path, lang, id, null);
                NormalizedPayload normalized = NormalizePayload(await GetJsonTokenAsync(uri).ConfigureAwait(false));
                AppendItems(allItems, normalized.Data);
            }

            JArray filtered = FilterByRevisedSince(allItems, revisedSince);
            JObject response = CreateEndpointPayload("productlicence", lang, format, null, filtered, ProductLicenceConfig.CsvColumns);
            response["summary"] = new JObject
            {
                ["idsRequested"] = new JArray(ids),
                ["pagesFetched"] = new JArray(),
                ["nextStartPage"] = JValue.CreateNull(),
                ["completed"] = true,
                ["elapsedMs"] = stopwatch.ElapsedMilliseconds
            };

            return CreateJsonResponse(HttpStatusCode.OK, response);
        }

        string fullUri = BuildEndpointUri(ProductLicenceConfig.Path, lang, null, null);
        string rawJson = await GetRawStringAsync(fullUri).ConfigureAwait(false);

        JObject fullResponse;
        if (format == ResultFormat.Csv)
        {
            int itemCount;
            string csv = ConvertProductLicenceArrayJsonToCsv(rawJson, revisedSince, out itemCount);
            fullResponse = new JObject
            {
                ["endpoint"] = "productlicence",
                ["lang"] = lang,
                ["format"] = "csv",
                ["itemCount"] = itemCount,
                ["metadata"] = JValue.CreateNull(),
                ["data"] = JValue.CreateNull(),
                ["csvContent"] = csv,
                ["summary"] = new JObject
                {
                    ["idsRequested"] = new JArray(),
                    ["pagesFetched"] = new JArray(),
                    ["nextStartPage"] = JValue.CreateNull(),
                    ["completed"] = true,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds
                }
            };
        }
        else
        {
            JToken payload = ParseJsonToken(rawJson);
            NormalizedPayload normalized = NormalizePayload(payload);
            JArray filtered = FilterByRevisedSince(normalized.Data, revisedSince);
            fullResponse = CreateEndpointPayload("productlicence", lang, ResultFormat.Json, normalized.Metadata, filtered, ProductLicenceConfig.CsvColumns);
            fullResponse["summary"] = new JObject
            {
                ["idsRequested"] = new JArray(),
                ["pagesFetched"] = new JArray(),
                ["nextStartPage"] = JValue.CreateNull(),
                ["completed"] = true,
                ["elapsedMs"] = stopwatch.ElapsedMilliseconds
            };
        }

        return CreateJsonResponse(HttpStatusCode.OK, fullResponse);
    }

    private JObject CreateEndpointPayload(
        string endpointName,
        string lang,
        ResultFormat format,
        JObject metadata,
        JArray data,
        string[] csvColumns)
    {
        return new JObject
        {
            ["endpoint"] = endpointName,
            ["lang"] = lang,
            ["format"] = format == ResultFormat.Csv ? "csv" : "json",
            ["itemCount"] = data.Count,
            ["metadata"] = metadata == null ? JValue.CreateNull() : metadata.DeepClone(),
            ["data"] = format == ResultFormat.Json ? data : JValue.CreateNull(),
            ["csvContent"] = format == ResultFormat.Csv ? BuildCsv(data, csvColumns) : JValue.CreateNull()
        };
    }

    private static JArray FilterByRevisedSince(JArray items, DateTime? revisedSince)
    {
        if (!revisedSince.HasValue)
        {
            return items;
        }

        JArray filtered = new JArray();
        foreach (JToken token in items)
        {
            JObject item = token as JObject;
            if (item == null)
            {
                continue;
            }

            string revisedDateText = item["revised_date"]?.ToString();
            DateTime revisedDate;
            if (!TryParseDate(revisedDateText, out revisedDate))
            {
                continue;
            }

            if (revisedDate.Date >= revisedSince.Value.Date)
            {
                filtered.Add(item.DeepClone());
            }
        }

        return filtered;
    }

    private static void AppendItems(JArray target, JArray items)
    {
        foreach (JToken item in items)
        {
            target.Add(item.DeepClone());
        }
    }

    private static NormalizedPayload NormalizePayload(JToken payload)
    {
        JObject metadata = null;
        JArray data;
        string nextPage = null;

        JObject objectPayload = payload as JObject;
        if (objectPayload != null && objectPayload["data"] is JArray)
        {
            metadata = objectPayload["metadata"] as JObject;
            data = (JArray)objectPayload["data"];
            nextPage = metadata? ["pagination"]? ["next"]?.ToString();
            return new NormalizedPayload(metadata, (JArray)data.DeepClone(), nextPage);
        }

        JArray arrayPayload = payload as JArray;
        if (arrayPayload != null)
        {
            return new NormalizedPayload(null, (JArray)arrayPayload.DeepClone(), null);
        }

        if (objectPayload != null)
        {
            return new NormalizedPayload(null, new JArray(objectPayload.DeepClone()), null);
        }

        return new NormalizedPayload(null, new JArray(payload.DeepClone()), null);
    }

    private async Task<JObject> ReadBodyObjectAsync()
    {
        string body = await this.Context.Request.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            return new JObject();
        }

        try
        {
            return JObject.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new ConnectorException(
                HttpStatusCode.BadRequest,
                "INVALID_REQUEST",
                "The request body must be valid JSON.",
                new JObject { ["details"] = ex.Message }
            );
        }
    }

    private async Task<JToken> GetJsonTokenAsync(string uri)
    {
        return ParseJsonToken(await GetRawStringAsync(uri).ConfigureAwait(false));
    }

    private async Task<string> GetRawStringAsync(string uri)
    {
        using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, uri))
        {
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            HttpResponseMessage response = await this.Context.SendAsync(request, this.CancellationToken).ConfigureAwait(false);
            string content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new ConnectorException(
                    response.StatusCode,
                    "UPSTREAM_ERROR",
                    "Health Canada returned an error response.",
                    new JObject
                    {
                        ["uri"] = uri,
                        ["statusCode"] = (int)response.StatusCode,
                        ["content"] = content
                    }
                );
            }

            return content;
        }
    }

    private static JToken ParseJsonToken(string json)
    {
        try
        {
            return JToken.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ConnectorException(
                HttpStatusCode.BadGateway,
                "INVALID_UPSTREAM_JSON",
                "The upstream response was not valid JSON.",
                new JObject { ["details"] = ex.Message }
            );
        }
    }

    private static string BuildEndpointUri(string path, string lang, string id, int? page)
    {
        var builder = new StringBuilder();
        builder.Append(BaseApiUrl);
        builder.Append(path);
        builder.Append("/?lang=");
        builder.Append(HttpUtility.UrlEncode(lang));
        builder.Append("&type=json");

        if (!string.IsNullOrWhiteSpace(id))
        {
            builder.Append("&id=");
            builder.Append(HttpUtility.UrlEncode(id));
        }
        else if (page.HasValue)
        {
            builder.Append("&page=");
            builder.Append(page.Value);
        }

        return builder.ToString();
    }

    private static List<string> ReadStringArray(JToken token)
    {
        var values = new List<string>();
        JArray array = token as JArray;
        if (array == null)
        {
            return values;
        }

        foreach (JToken item in array)
        {
            string value = item?.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(value.Trim());
            }
        }

        return values;
    }

    private static string NormalizeLanguage(string lang)
    {
        return string.Equals(lang, "fr", StringComparison.OrdinalIgnoreCase) ? "fr" : "en";
    }

    private static ResultFormat ParseResultFormat(string format)
    {
        return ParseResultFormat(format, ResultFormat.Json);
    }

    private static ResultFormat ParseResultFormat(string format, ResultFormat defaultValue)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return defaultValue;
        }

        if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            return ResultFormat.Csv;
        }

        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            return ResultFormat.Json;
        }

        throw new ConnectorException(
            HttpStatusCode.BadRequest,
            "INVALID_REQUEST",
            "The output format must be either 'json' or 'csv'.",
            null
        );
    }

    private static int? ParseOptionalPositiveInt(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        int parsed;
        if (!int.TryParse(value, out parsed) || parsed < 1)
        {
            throw new ConnectorException(
                HttpStatusCode.BadRequest,
                "INVALID_REQUEST",
                "The '" + parameterName + "' value must be a positive integer.",
                null
            );
        }

        return parsed;
    }

    private static DateTime? ParseOptionalDate(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        DateTime parsed;
        if (!TryParseDate(value, out parsed))
        {
            throw new ConnectorException(
                HttpStatusCode.BadRequest,
                "INVALID_REQUEST",
                "The '" + parameterName + "' value must be a valid date.",
                null
            );
        }

        return parsed.Date;
    }

    private static bool TryParseDate(string value, out DateTime parsed)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            parsed = default(DateTime);
            return false;
        }

        return DateTime.TryParse(value, out parsed);
    }

    private static string BuildCsv(JArray data, string[] columns)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(",", columns.Select(CsvEscape).ToArray()));

        foreach (JToken token in data)
        {
            JObject row = token as JObject;
            if (row == null)
            {
                continue;
            }

            string[] values = columns
                .Select(column => CsvEscape(row[column] == null ? string.Empty : row[column].ToString()))
                .ToArray();

            builder.AppendLine(string.Join(",", values));
        }

        return builder.ToString();
    }

    private static string ConvertProductLicenceArrayJsonToCsv(string json, DateTime? revisedSince, out int itemCount)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(",", ProductLicenceConfig.CsvColumns.Select(CsvEscape).ToArray()));
        itemCount = 0;

        using (var stringReader = new StringReader(json))
        using (var reader = new JsonTextReader(stringReader))
        {
            if (!reader.Read() || reader.TokenType != JsonToken.StartArray)
            {
                throw new ConnectorException(
                    HttpStatusCode.BadGateway,
                    "INVALID_UPSTREAM_JSON",
                    "The product licence export was expected to be a JSON array.",
                    null
                );
            }

            Dictionary<string, string> currentRow = null;
            string currentProperty = null;

            while (reader.Read())
            {
                if (reader.TokenType == JsonToken.StartObject)
                {
                    currentRow = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    currentProperty = null;
                }
                else if (reader.TokenType == JsonToken.PropertyName)
                {
                    currentProperty = Convert.ToString(reader.Value);
                }
                else if (
                    reader.TokenType == JsonToken.String ||
                    reader.TokenType == JsonToken.Integer ||
                    reader.TokenType == JsonToken.Float ||
                    reader.TokenType == JsonToken.Boolean ||
                    reader.TokenType == JsonToken.Date)
                {
                    if (currentRow != null && currentProperty != null)
                    {
                        currentRow[currentProperty] = reader.Value == null ? string.Empty : reader.Value.ToString();
                    }
                }
                else if (reader.TokenType == JsonToken.Null)
                {
                    if (currentRow != null && currentProperty != null)
                    {
                        currentRow[currentProperty] = string.Empty;
                    }
                }
                else if (reader.TokenType == JsonToken.EndObject)
                {
                    if (ShouldIncludeProductLicenceRow(currentRow, revisedSince))
                    {
                        string[] values = ProductLicenceConfig.CsvColumns
                            .Select(column => CsvEscape(currentRow.ContainsKey(column) ? currentRow[column] : string.Empty))
                            .ToArray();

                        builder.AppendLine(string.Join(",", values));
                        itemCount += 1;
                    }

                    currentRow = null;
                    currentProperty = null;
                }
            }
        }

        return builder.ToString();
    }

    private static bool ShouldIncludeProductLicenceRow(Dictionary<string, string> row, DateTime? revisedSince)
    {
        if (!revisedSince.HasValue)
        {
            return true;
        }

        string revisedDateText;
        if (!row.TryGetValue("revised_date", out revisedDateText))
        {
            return false;
        }

        DateTime revisedDate;
        if (!TryParseDate(revisedDateText, out revisedDate))
        {
            return false;
        }

        return revisedDate.Date >= revisedSince.Value.Date;
    }

    private static string CsvEscape(string value)
    {
        string text = value ?? string.Empty;
        bool needsQuotes = text.Contains(",") || text.Contains("\"") || text.Contains("\r") || text.Contains("\n");
        if (!needsQuotes)
        {
            return text;
        }

        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    private static HttpResponseMessage CreateJsonResponse(HttpStatusCode statusCode, JObject payload)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = CreateJsonContent(payload.ToString(Newtonsoft.Json.Formatting.None))
        };
    }

    private static HttpResponseMessage CreateErrorResponse(
        HttpStatusCode statusCode,
        string errorCode,
        string message,
        JObject details)
    {
        var error = new JObject
        {
            ["error"] = new JObject
            {
                ["code"] = errorCode,
                ["message"] = message,
                ["details"] = details == null ? JValue.CreateNull() : details
            }
        };

        return new HttpResponseMessage(statusCode)
        {
            Content = CreateJsonContent(error.ToString(Newtonsoft.Json.Formatting.None))
        };
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

    private enum ResultFormat
    {
        Json,
        Csv
    }

    private sealed class EndpointConfig
    {
        public EndpointConfig(string path, bool supportsPage, bool requiresId, string[] csvColumns)
        {
            this.Path = path;
            this.SupportsPage = supportsPage;
            this.RequiresId = requiresId;
            this.CsvColumns = csvColumns;
        }

        public string Path { get; private set; }

        public bool SupportsPage { get; private set; }

        public bool RequiresId { get; private set; }

        public string[] CsvColumns { get; private set; }
    }

    private sealed class NormalizedPayload
    {
        public NormalizedPayload(JObject metadata, JArray data, string nextPage)
        {
            this.Metadata = metadata;
            this.Data = data;
            this.NextPage = nextPage;
        }

        public JObject Metadata { get; private set; }

        public JArray Data { get; private set; }

        public string NextPage { get; private set; }
    }

    private sealed class ConnectorException : Exception
    {
        public ConnectorException(HttpStatusCode statusCode, string code, string message, JObject details)
            : base(message)
        {
            this.StatusCode = statusCode;
            this.Code = code;
            this.Details = details;
        }

        public HttpStatusCode StatusCode { get; private set; }

        public string Code { get; private set; }

        public JObject Details { get; private set; }
    }
}