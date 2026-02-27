using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class Script : ScriptBase
{
    private const int DefaultMaxOutputRows = 100000;
    private const int AbsoluteMaxOutputRows = 1000000;

    public override async Task<HttpResponseMessage> ExecuteAsync()
    {
        string operationId = DecodeOperationId(this.Context.OperationId);
        switch (operationId)
        {
            case "ExecuteDatasetSqlQuery":
                return await HandleExecuteDatasetSqlQueryAsync().ConfigureAwait(false);
            default:
                return CreateErrorResponse(
                    HttpStatusCode.BadRequest,
                    "UNKNOWN_OPERATION",
                    "Unknown operation: " + operationId,
                    null
                );
        }
    }

    private async Task<HttpResponseMessage> HandleExecuteDatasetSqlQueryAsync()
    {
        DateTime started = DateTime.UtcNow;

        try
        {
            string content = await this.Context.Request.Content.ReadAsStringAsync().ConfigureAwait(false);
            JObject body = ParseBody(content);

            string sql = body["sql"]?.ToString();
            string outputMode = body["outputMode"]?.ToString();
            JArray datasetArray = body["datasets"] as JArray;
            JObject engineOptionsBody = body["engineOptions"] as JObject;

            if (string.IsNullOrWhiteSpace(sql))
            {
                return CreateErrorResponse(HttpStatusCode.BadRequest, "INVALID_REQUEST", "The 'sql' field is required.", null);
            }

            if (datasetArray == null || datasetArray.Count == 0)
            {
                return CreateErrorResponse(HttpStatusCode.BadRequest, "INVALID_REQUEST", "The 'datasets' array must contain at least one dataset.", null);
            }

            OutputMode mode;
            if (!TryParseOutputMode(outputMode, out mode))
            {
                return CreateErrorResponse(
                    HttpStatusCode.BadRequest,
                    "INVALID_REQUEST",
                    "The 'outputMode' field must be one of: Csv, RowsAndSchema, ObjectArray.",
                    null
                );
            }

            QueryExecutionOptions options = ParseExecutionOptions(engineOptionsBody);
            Dictionary<string, RowSet> sourceTables = LoadDatasets(datasetArray, options);

            SqlParser parser = new SqlParser(sql);
            QueryNode query = parser.ParseQuery();

            QueryExecutor executor = new QueryExecutor(options);
            RowSet result = executor.ExecuteQuery(query, sourceTables);

            if (result.Rows.Count > options.MaxOutputRows)
            {
                return CreateErrorResponse(
                    HttpStatusCode.BadRequest,
                    "RESULT_LIMIT_EXCEEDED",
                    "Query produced " + result.Rows.Count + " rows which exceeds maxOutputRows (" + options.MaxOutputRows + ").",
                    new JObject { ["rowCount"] = result.Rows.Count, ["maxOutputRows"] = options.MaxOutputRows }
                );
            }

            JObject responsePayload = new JObject();
            responsePayload["mode"] = mode.ToString();
            responsePayload["rowCount"] = result.Rows.Count;
            responsePayload["durationMs"] = (int)(DateTime.UtcNow - started).TotalMilliseconds;
            responsePayload["result"] = FormatResult(mode, result);

            return CreateJsonResponse(HttpStatusCode.OK, responsePayload);
        }
        catch (SqlParseException ex)
        {
            return CreateErrorResponse(HttpStatusCode.BadRequest, "SQL_PARSE_ERROR", ex.Message, new JObject { ["position"] = ex.Position });
        }
        catch (SqlValidationException ex)
        {
            return CreateErrorResponse(HttpStatusCode.BadRequest, "SQL_VALIDATION_ERROR", ex.Message, null);
        }
        catch (DatasetException ex)
        {
            return CreateErrorResponse(HttpStatusCode.BadRequest, ex.Code, ex.Message, ex.Details);
        }
        catch (SqlExecutionException ex)
        {
            return CreateErrorResponse(HttpStatusCode.BadRequest, ex.Code, ex.Message, ex.Details);
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(HttpStatusCode.InternalServerError, "EXECUTION_ERROR", "Unexpected execution failure: " + ex.Message, null);
        }
    }

    private static JObject ParseBody(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new DatasetException("INVALID_REQUEST", "Request body is required.", null);
        }

        try
        {
            return JObject.Parse(content);
        }
        catch (JsonException ex)
        {
            throw new DatasetException("INVALID_REQUEST", "Request body is not valid JSON: " + ex.Message, null);
        }
    }

    private static bool TryParseOutputMode(string value, out OutputMode mode)
    {
        mode = OutputMode.RowsAndSchema;
        if (string.IsNullOrWhiteSpace(value)) return false;

        if (value.Equals("Csv", StringComparison.OrdinalIgnoreCase))
        {
            mode = OutputMode.Csv;
            return true;
        }
        if (value.Equals("RowsAndSchema", StringComparison.OrdinalIgnoreCase))
        {
            mode = OutputMode.RowsAndSchema;
            return true;
        }
        if (value.Equals("ObjectArray", StringComparison.OrdinalIgnoreCase))
        {
            mode = OutputMode.ObjectArray;
            return true;
        }
        return false;
    }

    private static QueryExecutionOptions ParseExecutionOptions(JObject optionsBody)
    {
        QueryExecutionOptions options = new QueryExecutionOptions();
        options.CaseSensitiveIdentifiers = optionsBody?["caseSensitiveIdentifiers"]?.ToObject<bool>() ?? false;
        string nullsSort = optionsBody?["nullsSort"]?.ToString();
        options.NullsSortFirst = string.Equals(nullsSort, "First", StringComparison.OrdinalIgnoreCase);
        options.MaxOutputRows = optionsBody?["maxOutputRows"]?.ToObject<int?>() ?? DefaultMaxOutputRows;

        if (options.MaxOutputRows < 1) options.MaxOutputRows = DefaultMaxOutputRows;
        if (options.MaxOutputRows > AbsoluteMaxOutputRows) options.MaxOutputRows = AbsoluteMaxOutputRows;

        return options;
    }

    private Dictionary<string, RowSet> LoadDatasets(JArray datasetArray, QueryExecutionOptions options)
    {
        Dictionary<string, RowSet> tables = options.CaseSensitiveIdentifiers
            ? new Dictionary<string, RowSet>(StringComparer.Ordinal)
            : new Dictionary<string, RowSet>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < datasetArray.Count; i++)
        {
            JObject datasetObj = datasetArray[i] as JObject;
            if (datasetObj == null)
            {
                throw new DatasetException("INVALID_DATASET", "Each datasets item must be an object.", new JObject { ["index"] = i });
            }

            string name = datasetObj["name"]?.ToString();
            string format = datasetObj["format"]?.ToString();
            JObject csvOptionsObj = datasetObj["csvOptions"] as JObject;
            JObject typeOverridesObj = datasetObj["typeOverrides"] as JObject;

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new DatasetException("INVALID_DATASET", "Dataset name is required.", new JObject { ["index"] = i });
            }
            if (!Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$"))
            {
                throw new DatasetException("INVALID_DATASET", "Dataset name '" + name + "' is invalid.", new JObject { ["index"] = i, ["name"] = name });
            }
            if (tables.ContainsKey(name))
            {
                throw new DatasetException("INVALID_DATASET", "Duplicate dataset name: " + name, new JObject { ["index"] = i, ["name"] = name });
            }

            RowSet table;
            if (string.Equals(format, "Json", StringComparison.OrdinalIgnoreCase))
            {
                JArray rows = datasetObj["jsonRows"] as JArray;
                table = BuildRowSetFromJson(name, rows, typeOverridesObj, false, options);
            }
            else if (string.Equals(format, "Csv", StringComparison.OrdinalIgnoreCase))
            {
                string csvText = datasetObj["csvText"]?.ToString();
                CsvOptions csvOptions = ParseCsvOptions(csvOptionsObj);
                table = BuildRowSetFromCsv(name, csvText, csvOptions, typeOverridesObj, options);
            }
            else
            {
                throw new DatasetException("INVALID_DATASET", "Dataset '" + name + "' has unsupported format. Use Json or Csv.", null);
            }

            tables[name] = table;
        }

        return tables;
    }

    private static CsvOptions ParseCsvOptions(JObject csvOptionsObj)
    {
        CsvOptions options = new CsvOptions();
        string separator = csvOptionsObj?["separator"]?.ToString();
        string quote = csvOptionsObj?["quote"]?.ToString();
        string lineBreak = csvOptionsObj?["lineBreak"]?.ToString();

        options.Separator = string.IsNullOrEmpty(separator) ? ',' : separator[0];
        options.Quote = string.IsNullOrEmpty(quote) ? '"' : quote[0];
        options.LineBreakMode = string.IsNullOrWhiteSpace(lineBreak) ? "Auto" : lineBreak;
        options.FirstRowIsHeader = csvOptionsObj?["firstRowIsHeader"]?.ToObject<bool?>() ?? true;
        options.AllColumnsAsString = csvOptionsObj?["allColumnsAsString"]?.ToObject<bool?>() ?? false;

        return options;
    }

    private RowSet BuildRowSetFromJson(string tableName, JArray rows, JObject typeOverridesObj, bool allColumnsAsString, QueryExecutionOptions options)
    {
        if (rows == null)
        {
            throw new DatasetException("INVALID_DATASET", "Dataset '" + tableName + "' with Json format requires 'jsonRows'.", null);
        }

        List<string> columnNames = new List<string>();
        Dictionary<string, int> columnIndexes = options.CaseSensitiveIdentifiers
            ? new Dictionary<string, int>(StringComparer.Ordinal)
            : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < rows.Count; i++)
        {
            JObject rowObj = rows[i] as JObject;
            if (rowObj == null)
            {
                throw new DatasetException("INVALID_DATASET", "jsonRows must contain JSON objects only.", new JObject { ["table"] = tableName, ["index"] = i });
            }

            foreach (JProperty property in rowObj.Properties())
            {
                if (!columnIndexes.ContainsKey(property.Name))
                {
                    columnIndexes[property.Name] = columnNames.Count;
                    columnNames.Add(property.Name);
                }
            }
        }

        RowSet rowSet = new RowSet();
        rowSet.Name = tableName;
        rowSet.Columns = columnNames.Select(c => new SqlColumn { Name = c, TableAlias = tableName, DataType = SqlDataType.Null, Nullable = true }).ToList();
        rowSet.Rows = new List<object[]>();

        for (int i = 0; i < rows.Count; i++)
        {
            JObject rowObj = (JObject)rows[i];
            object[] values = new object[columnNames.Count];

            foreach (JProperty property in rowObj.Properties())
            {
                int colIndex;
                if (!columnIndexes.TryGetValue(property.Name, out colIndex)) continue;
                values[colIndex] = NormalizeJToken(property.Value);
            }

            rowSet.Rows.Add(values);
        }

        ApplyInferredTypes(rowSet, typeOverridesObj, allColumnsAsString, options);
        return rowSet;
    }

    private RowSet BuildRowSetFromCsv(string tableName, string csvText, CsvOptions csvOptions, JObject typeOverridesObj, QueryExecutionOptions options)
    {
        if (csvText == null)
        {
            throw new DatasetException("INVALID_DATASET", "Dataset '" + tableName + "' with Csv format requires 'csvText'.", null);
        }

        List<List<string>> parsedRows = ParseCsv(csvText, csvOptions);
        if (parsedRows.Count == 0)
        {
            return new RowSet
            {
                Name = tableName,
                Columns = new List<SqlColumn>(),
                Rows = new List<object[]>()
            };
        }

        List<string> columnNames = new List<string>();
        int dataStartIndex = 0;

        if (csvOptions.FirstRowIsHeader)
        {
            List<string> header = parsedRows[0];
            for (int i = 0; i < header.Count; i++)
            {
                string name = string.IsNullOrWhiteSpace(header[i]) ? ("column" + (i + 1)) : header[i].Trim();
                if (!Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$"))
                {
                    name = NormalizeColumnName(name, i + 1);
                }
                columnNames.Add(name);
            }
            dataStartIndex = 1;
        }
        else
        {
            int colCount = parsedRows[0].Count;
            for (int i = 0; i < colCount; i++) columnNames.Add("column" + (i + 1));
        }

        RowSet rowSet = new RowSet();
        rowSet.Name = tableName;
        rowSet.Columns = columnNames.Select(c => new SqlColumn { Name = c, TableAlias = tableName, DataType = SqlDataType.Null, Nullable = true }).ToList();
        rowSet.Rows = new List<object[]>();

        for (int r = dataStartIndex; r < parsedRows.Count; r++)
        {
            List<string> raw = parsedRows[r];
            object[] values = new object[columnNames.Count];
            for (int c = 0; c < columnNames.Count; c++)
            {
                values[c] = c < raw.Count ? raw[c] : null;
            }
            rowSet.Rows.Add(values);
        }

        ApplyInferredTypes(rowSet, typeOverridesObj, csvOptions.AllColumnsAsString, options);
        return rowSet;
    }

    private static string NormalizeColumnName(string raw, int index)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "column" + index;
        string normalized = Regex.Replace(raw.Trim(), "[^A-Za-z0-9_]", "_");
        if (!Regex.IsMatch(normalized, "^[A-Za-z_]")) normalized = "c_" + normalized;
        if (normalized.Length == 0) normalized = "column" + index;
        return normalized;
    }
    private List<List<string>> ParseCsv(string text, CsvOptions options)
    {
        List<List<string>> rows = new List<List<string>>();
        List<string> row = new List<string>();
        StringBuilder field = new StringBuilder();

        bool inQuotes = false;
        int i = 0;
        while (i < text.Length)
        {
            char ch = text[i];

            if (inQuotes)
            {
                if (ch == options.Quote)
                {
                    if (i + 1 < text.Length && text[i + 1] == options.Quote)
                    {
                        field.Append(options.Quote);
                        i += 2;
                        continue;
                    }

                    inQuotes = false;
                    i++;
                    continue;
                }

                field.Append(ch);
                i++;
                continue;
            }

            if (ch == options.Quote)
            {
                inQuotes = true;
                i++;
                continue;
            }

            if (ch == options.Separator)
            {
                row.Add(field.ToString());
                field.Length = 0;
                i++;
                continue;
            }

            int breakLen;
            if (TryReadLineBreak(text, i, options.LineBreakMode, out breakLen))
            {
                row.Add(field.ToString());
                field.Length = 0;
                rows.Add(row);
                row = new List<string>();
                i += breakLen;
                continue;
            }

            field.Append(ch);
            i++;
        }

        if (inQuotes)
        {
            throw new DatasetException("CSV_PARSE_ERROR", "Unterminated quoted field in CSV payload.", null);
        }

        row.Add(field.ToString());
        if (row.Count > 1 || row[0].Length > 0 || rows.Count > 0)
        {
            rows.Add(row);
        }

        return rows;
    }

    private static bool TryReadLineBreak(string text, int position, string lineBreakMode, out int length)
    {
        length = 0;
        bool auto = string.IsNullOrWhiteSpace(lineBreakMode) || lineBreakMode.Equals("Auto", StringComparison.OrdinalIgnoreCase);

        if (auto)
        {
            if (position + 1 < text.Length && text[position] == '\r' && text[position + 1] == '\n')
            {
                length = 2;
                return true;
            }
            if (text[position] == '\n' || text[position] == '\r')
            {
                length = 1;
                return true;
            }
            return false;
        }

        if (lineBreakMode.Equals("LF", StringComparison.OrdinalIgnoreCase) && text[position] == '\n')
        {
            length = 1;
            return true;
        }
        if (lineBreakMode.Equals("CR", StringComparison.OrdinalIgnoreCase) && text[position] == '\r')
        {
            length = 1;
            return true;
        }
        if (lineBreakMode.Equals("CRLF", StringComparison.OrdinalIgnoreCase))
        {
            if (position + 1 < text.Length && text[position] == '\r' && text[position + 1] == '\n')
            {
                length = 2;
                return true;
            }
        }

        return false;
    }

    private static object NormalizeJToken(JToken token)
    {
        if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined) return null;
        if (token.Type == JTokenType.Integer) return token.ToObject<long>();
        if (token.Type == JTokenType.Float) return token.ToObject<double>();
        if (token.Type == JTokenType.Boolean) return token.ToObject<bool>();
        if (token.Type == JTokenType.Date) return token.ToObject<DateTime>();
        if (token.Type == JTokenType.String) return token.ToObject<string>();
        return token.ToString(Newtonsoft.Json.Formatting.None);
    }

    private static void ApplyInferredTypes(RowSet rowSet, JObject typeOverridesObj, bool allColumnsAsString, QueryExecutionOptions options)
    {
        Dictionary<string, SqlDataType> overrides = ParseTypeOverrides(typeOverridesObj, options.CaseSensitiveIdentifiers);

        for (int c = 0; c < rowSet.Columns.Count; c++)
        {
            SqlColumn column = rowSet.Columns[c];
            SqlDataType targetType;

            if (overrides.TryGetValue(column.Name, out targetType))
            {
            }
            else if (allColumnsAsString)
            {
                targetType = SqlDataType.String;
            }
            else
            {
                targetType = InferColumnType(rowSet.Rows, c);
            }

            column.DataType = targetType;

            for (int r = 0; r < rowSet.Rows.Count; r++)
            {
                rowSet.Rows[r][c] = ConvertToType(rowSet.Rows[r][c], targetType);
            }
        }
    }

    private static Dictionary<string, SqlDataType> ParseTypeOverrides(JObject overridesObj, bool caseSensitive)
    {
        Dictionary<string, SqlDataType> map = caseSensitive
            ? new Dictionary<string, SqlDataType>(StringComparer.Ordinal)
            : new Dictionary<string, SqlDataType>(StringComparer.OrdinalIgnoreCase);

        if (overridesObj == null) return map;

        foreach (JProperty property in overridesObj.Properties())
        {
            SqlDataType parsed;
            if (TryParseDataType(property.Value?.ToString(), out parsed))
            {
                map[property.Name] = parsed;
            }
        }

        return map;
    }

    private static bool TryParseDataType(string value, out SqlDataType dataType)
    {
        dataType = SqlDataType.String;
        if (string.IsNullOrWhiteSpace(value)) return false;

        switch (value.Trim().ToLowerInvariant())
        {
            case "string":
                dataType = SqlDataType.String;
                return true;
            case "int64":
            case "long":
            case "integer":
                dataType = SqlDataType.Int64;
                return true;
            case "double":
            case "float":
                dataType = SqlDataType.Double;
                return true;
            case "decimal":
                dataType = SqlDataType.Decimal;
                return true;
            case "boolean":
            case "bool":
                dataType = SqlDataType.Boolean;
                return true;
            case "datetime":
            case "date":
                dataType = SqlDataType.DateTime;
                return true;
            case "null":
                dataType = SqlDataType.Null;
                return true;
            default:
                return false;
        }
    }

    private static SqlDataType InferColumnType(List<object[]> rows, int columnIndex)
    {
        bool hasString = false;
        bool hasDate = false;
        bool hasBool = false;
        bool hasInt = false;
        bool hasDouble = false;

        for (int i = 0; i < rows.Count; i++)
        {
            object value = rows[i][columnIndex];
            if (value == null) continue;

            if (value is long || value is int || value is short)
            {
                hasInt = true;
                continue;
            }

            if (value is double || value is float || value is decimal)
            {
                hasDouble = true;
                continue;
            }

            if (value is bool)
            {
                hasBool = true;
                continue;
            }

            if (value is DateTime)
            {
                hasDate = true;
                continue;
            }

            string text = value.ToString();
            long parsedLong;
            double parsedDouble;
            bool parsedBool;
            DateTime parsedDate;

            if (long.TryParse(text, out parsedLong)) hasInt = true;
            else if (double.TryParse(text, out parsedDouble)) hasDouble = true;
            else if (bool.TryParse(text, out parsedBool)) hasBool = true;
            else if (DateTime.TryParse(text, out parsedDate)) hasDate = true;
            else hasString = true;
        }

        if (hasString) return SqlDataType.String;
        if (hasDate && !hasBool && !hasInt && !hasDouble) return SqlDataType.DateTime;
        if (hasBool && !hasInt && !hasDouble && !hasDate) return SqlDataType.Boolean;
        if (hasDouble) return SqlDataType.Double;
        if (hasInt) return SqlDataType.Int64;
        return SqlDataType.Null;
    }

    private static object ConvertToType(object value, SqlDataType dataType)
    {
        if (value == null) return null;
        if (dataType == SqlDataType.Null) return null;
        if (dataType == SqlDataType.String) return value.ToString();

        try
        {
            string text = value.ToString();
            long l;
            double d;
            decimal m;
            bool b;
            DateTime dt;

            switch (dataType)
            {
                case SqlDataType.Int64:
                    if (value is long) return value;
                    if (long.TryParse(text, out l)) return l;
                    return null;
                case SqlDataType.Double:
                    if (value is double) return value;
                    if (value is float) return Convert.ToDouble(value);
                    if (double.TryParse(text, out d)) return d;
                    return null;
                case SqlDataType.Decimal:
                    if (value is decimal) return value;
                    if (decimal.TryParse(text, out m)) return m;
                    return null;
                case SqlDataType.Boolean:
                    if (value is bool) return value;
                    if (bool.TryParse(text, out b)) return b;
                    if (text == "1") return true;
                    if (text == "0") return false;
                    return null;
                case SqlDataType.DateTime:
                    if (value is DateTime) return value;
                    if (DateTime.TryParse(text, out dt)) return dt;
                    return null;
                default:
                    return value;
            }
        }
        catch
        {
            return null;
        }
    }

    private JObject FormatResult(OutputMode mode, RowSet result)
    {
        switch (mode)
        {
            case OutputMode.Csv:
                return new JObject { ["csv"] = RenderCsv(result) };
            case OutputMode.ObjectArray:
                return new JObject { ["rows"] = RenderObjectRows(result) };
            default:
                return new JObject
                {
                    ["schema"] = RenderSchema(result),
                    ["rows"] = RenderRowArrays(result)
                };
        }
    }

    private static JArray RenderSchema(RowSet result)
    {
        JArray schema = new JArray();
        for (int i = 0; i < result.Columns.Count; i++)
        {
            SqlColumn c = result.Columns[i];
            JObject col = new JObject();
            col["name"] = c.Name;
            col["type"] = c.DataType.ToString();
            col["nullable"] = c.Nullable;
            schema.Add(col);
        }
        return schema;
    }

    private static JArray RenderRowArrays(RowSet result)
    {
        JArray rows = new JArray();
        for (int r = 0; r < result.Rows.Count; r++)
        {
            object[] row = result.Rows[r];
            JArray outRow = new JArray();
            for (int c = 0; c < result.Columns.Count; c++) outRow.Add(ToJToken(row[c]));
            rows.Add(outRow);
        }
        return rows;
    }

    private static JArray RenderObjectRows(RowSet result)
    {
        JArray rows = new JArray();
        for (int r = 0; r < result.Rows.Count; r++)
        {
            object[] row = result.Rows[r];
            JObject obj = new JObject();
            for (int c = 0; c < result.Columns.Count; c++) obj[result.Columns[c].Name] = ToJToken(row[c]);
            rows.Add(obj);
        }
        return rows;
    }

    private static JToken ToJToken(object value)
    {
        if (value == null) return JValue.CreateNull();
        if (value is DateTime) return new JValue(((DateTime)value).ToString("o"));
        return JToken.FromObject(value);
    }

    private static string RenderCsv(RowSet result)
    {
        StringBuilder sb = new StringBuilder();

        for (int c = 0; c < result.Columns.Count; c++)
        {
            if (c > 0) sb.Append(',');
            sb.Append(EscapeCsv(result.Columns[c].Name));
        }
        sb.Append("\r\n");

        for (int r = 0; r < result.Rows.Count; r++)
        {
            object[] row = result.Rows[r];
            for (int c = 0; c < result.Columns.Count; c++)
            {
                if (c > 0) sb.Append(',');
                sb.Append(EscapeCsv(FormatValueAsString(row[c])));
            }
            if (r < result.Rows.Count - 1) sb.Append("\r\n");
        }

        return sb.ToString();
    }

    private static string FormatValueAsString(object value)
    {
        if (value == null) return string.Empty;
        if (value is DateTime) return ((DateTime)value).ToString("o");
        return value.ToString();
    }

    private static string EscapeCsv(string value)
    {
        if (value == null) return string.Empty;

        bool mustQuote = value.Contains(",") || value.Contains("\r") || value.Contains("\n") || value.Contains("\"");
        if (!mustQuote) return value;

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string DecodeOperationId(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId)) return operationId;

        try
        {
            byte[] data = Convert.FromBase64String(operationId);
            return Encoding.UTF8.GetString(data);
        }
        catch
        {
            return operationId;
        }
    }

    private HttpResponseMessage CreateJsonResponse(HttpStatusCode statusCode, JObject body)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = CreateJsonContent(body.ToString(Newtonsoft.Json.Formatting.None))
        };
    }

    private HttpResponseMessage CreateErrorResponse(HttpStatusCode statusCode, string code, string message, JObject details)
    {
        JObject payload = new JObject();
        JObject error = new JObject();
        error["code"] = code;
        error["message"] = message;
        if (details != null) error["details"] = details;
        payload["error"] = error;
        return CreateJsonResponse(statusCode, payload);
    }

    private StringContent CreateJsonContent(string json)
    {
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private enum OutputMode
    {
        Csv,
        RowsAndSchema,
        ObjectArray
    }

    private enum SqlDataType
    {
        Null,
        String,
        Int64,
        Double,
        Decimal,
        Boolean,
        DateTime
    }

    private sealed class QueryExecutionOptions
    {
        public bool CaseSensitiveIdentifiers { get; set; }
        public bool NullsSortFirst { get; set; }
        public int MaxOutputRows { get; set; }
    }

    private sealed class CsvOptions
    {
        public char Separator { get; set; }
        public char Quote { get; set; }
        public string LineBreakMode { get; set; }
        public bool FirstRowIsHeader { get; set; }
        public bool AllColumnsAsString { get; set; }
    }

    private sealed class SqlColumn
    {
        public string Name { get; set; }
        public string TableAlias { get; set; }
        public SqlDataType DataType { get; set; }
        public bool Nullable { get; set; }
    }

    private sealed class RowSet
    {
        public string Name { get; set; }
        public List<SqlColumn> Columns { get; set; }
        public List<object[]> Rows { get; set; }

        public RowSet Clone()
        {
            RowSet clone = new RowSet();
            clone.Name = this.Name;
            clone.Columns = new List<SqlColumn>(this.Columns.Count);
            clone.Rows = new List<object[]>(this.Rows.Count);

            for (int i = 0; i < this.Columns.Count; i++)
            {
                SqlColumn c = this.Columns[i];
                clone.Columns.Add(new SqlColumn
                {
                    Name = c.Name,
                    TableAlias = c.TableAlias,
                    DataType = c.DataType,
                    Nullable = c.Nullable
                });
            }

            for (int i = 0; i < this.Rows.Count; i++)
            {
                object[] row = this.Rows[i];
                object[] copy = new object[row.Length];
                Array.Copy(row, copy, row.Length);
                clone.Rows.Add(copy);
            }

            return clone;
        }
    }

    private sealed class DatasetException : Exception
    {
        public DatasetException(string code, string message, JObject details) : base(message)
        {
            this.Code = code;
            this.Details = details;
        }

        public string Code { get; private set; }
        public JObject Details { get; private set; }
    }

    private sealed class SqlExecutionException : Exception
    {
        public SqlExecutionException(string code, string message, JObject details) : base(message)
        {
            this.Code = code;
            this.Details = details;
        }

        public string Code { get; private set; }
        public JObject Details { get; private set; }
    }

    private sealed class SqlValidationException : Exception
    {
        public SqlValidationException(string message) : base(message) { }
    }

    private sealed class SqlParseException : Exception
    {
        public SqlParseException(string message, int position) : base(message)
        {
            this.Position = position;
        }

        public int Position { get; private set; }
    }
    private enum TokenKind
    {
        Identifier,
        Number,
        String,
        Comma,
        Dot,
        Star,
        Plus,
        Minus,
        Slash,
        Percent,
        LParen,
        RParen,
        Semicolon,
        Equal,
        NotEqual,
        Less,
        LessEqual,
        Greater,
        GreaterEqual,
        End
    }

    private sealed class Token
    {
        public TokenKind Kind { get; set; }
        public string Text { get; set; }
        public bool IsKeyword { get; set; }
        public int Position { get; set; }
    }

    private sealed class QueryNode
    {
        public List<CteNode> Ctes { get; set; }
        public QueryTermNode Term { get; set; }
        public List<OrderItem> OrderBy { get; set; }
        public int? Limit { get; set; }
        public int? Offset { get; set; }
    }

    private sealed class CteNode
    {
        public string Name { get; set; }
        public QueryNode Query { get; set; }
    }

    private abstract class QueryTermNode { }

    private sealed class SelectNode : QueryTermNode
    {
        public bool Distinct { get; set; }
        public List<SelectItemNode> SelectItems { get; set; }
        public FromNode From { get; set; }
        public ExprNode Where { get; set; }
        public List<ExprNode> GroupBy { get; set; }
        public ExprNode Having { get; set; }
    }

    private sealed class SetTermNode : QueryTermNode
    {
        public QueryTermNode Left { get; set; }
        public QueryTermNode Right { get; set; }
        public string Operator { get; set; }
        public bool All { get; set; }
    }

    private sealed class SelectItemNode
    {
        public bool IsWildcard { get; set; }
        public string WildcardQualifier { get; set; }
        public ExprNode Expression { get; set; }
        public string Alias { get; set; }
    }

    private sealed class FromNode
    {
        public TableSourceNode Source { get; set; }
        public List<JoinNode> Joins { get; set; }
    }

    private abstract class TableSourceNode
    {
        public string Alias { get; set; }
    }

    private sealed class NamedTableSourceNode : TableSourceNode
    {
        public string Name { get; set; }
    }

    private sealed class SubqueryTableSourceNode : TableSourceNode
    {
        public QueryNode Query { get; set; }
    }

    private sealed class JoinNode
    {
        public string JoinType { get; set; }
        public TableSourceNode Source { get; set; }
        public ExprNode On { get; set; }
    }

    private sealed class OrderItem
    {
        public ExprNode Expression { get; set; }
        public bool Desc { get; set; }
    }

    private abstract class ExprNode
    {
        public int Id { get; set; }
    }

    private sealed class LiteralExprNode : ExprNode
    {
        public object Value { get; set; }
    }

    private sealed class IdentifierExprNode : ExprNode
    {
        public string Qualifier { get; set; }
        public string Name { get; set; }
    }

    private sealed class UnaryExprNode : ExprNode
    {
        public string Operator { get; set; }
        public ExprNode Operand { get; set; }
    }

    private sealed class BinaryExprNode : ExprNode
    {
        public string Operator { get; set; }
        public ExprNode Left { get; set; }
        public ExprNode Right { get; set; }
    }

    private sealed class FunctionExprNode : ExprNode
    {
        public string Name { get; set; }
        public List<ExprNode> Arguments { get; set; }
        public bool Distinct { get; set; }
    }

    private sealed class WindowExprNode : ExprNode
    {
        public FunctionExprNode Function { get; set; }
        public WindowSpecNode Spec { get; set; }
    }

    private sealed class WindowSpecNode
    {
        public List<ExprNode> PartitionBy { get; set; }
        public List<OrderItem> OrderBy { get; set; }
    }

    private sealed class IsNullExprNode : ExprNode
    {
        public ExprNode Expression { get; set; }
        public bool Negated { get; set; }
    }

    private sealed class LikeExprNode : ExprNode
    {
        public ExprNode Input { get; set; }
        public ExprNode Pattern { get; set; }
        public bool Negated { get; set; }
    }

    private sealed class BetweenExprNode : ExprNode
    {
        public ExprNode Input { get; set; }
        public ExprNode Low { get; set; }
        public ExprNode High { get; set; }
        public bool Negated { get; set; }
    }

    private sealed class InListExprNode : ExprNode
    {
        public ExprNode Input { get; set; }
        public List<ExprNode> Values { get; set; }
        public bool Negated { get; set; }
    }

    private sealed class InSubqueryExprNode : ExprNode
    {
        public ExprNode Input { get; set; }
        public QueryNode Subquery { get; set; }
        public bool Negated { get; set; }
    }

    private sealed class SubqueryExprNode : ExprNode
    {
        public QueryNode Query { get; set; }
    }

    private sealed class CaseExprNode : ExprNode
    {
        public List<CaseWhenNode> Whens { get; set; }
        public ExprNode Else { get; set; }
    }

    private sealed class CaseWhenNode
    {
        public ExprNode When { get; set; }
        public ExprNode Then { get; set; }
    }

    private sealed class StarExprNode : ExprNode { }

    private static class SqlTokenizer
    {
        public static readonly HashSet<string> KeywordSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SELECT","FROM","WHERE","GROUP","BY","HAVING","ORDER","ASC","DESC","LIMIT","OFFSET",
            "JOIN","INNER","LEFT","RIGHT","FULL","OUTER","CROSS","ON",
            "AS","DISTINCT","WITH","UNION","ALL","INTERSECT","EXCEPT",
            "AND","OR","NOT","IS","NULL","IN","LIKE","BETWEEN",
            "CASE","WHEN","THEN","ELSE","END","OVER","PARTITION",
            "TRUE","FALSE"
        };

        public static List<Token> Tokenize(string sql)
        {
            List<Token> tokens = new List<Token>();
            int i = 0;
            while (i < sql.Length)
            {
                char ch = sql[i];

                if (char.IsWhiteSpace(ch))
                {
                    i++;
                    continue;
                }

                if (ch == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
                {
                    i += 2;
                    while (i < sql.Length && sql[i] != '\n' && sql[i] != '\r') i++;
                    continue;
                }

                if (ch == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/')) i++;
                    i = Math.Min(i + 2, sql.Length);
                    continue;
                }

                if (char.IsLetter(ch) || ch == '_')
                {
                    int start = i;
                    i++;
                    while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_')) i++;
                    string text = sql.Substring(start, i - start);
                    bool isKeyword = KeywordSet.Contains(text);
                    tokens.Add(new Token
                    {
                        Kind = TokenKind.Identifier,
                        Text = isKeyword ? text.ToUpperInvariant() : text,
                        IsKeyword = isKeyword,
                        Position = start
                    });
                    continue;
                }

                if (char.IsDigit(ch))
                {
                    int start = i;
                    i++;
                    while (i < sql.Length && char.IsDigit(sql[i])) i++;
                    if (i < sql.Length && sql[i] == '.')
                    {
                        i++;
                        while (i < sql.Length && char.IsDigit(sql[i])) i++;
                    }
                    tokens.Add(new Token { Kind = TokenKind.Number, Text = sql.Substring(start, i - start), Position = start });
                    continue;
                }

                if (ch == '\'')
                {
                    int start = i;
                    i++;
                    StringBuilder literal = new StringBuilder();
                    bool closed = false;
                    while (i < sql.Length)
                    {
                        if (sql[i] == '\'')
                        {
                            if (i + 1 < sql.Length && sql[i + 1] == '\'')
                            {
                                literal.Append('\'');
                                i += 2;
                                continue;
                            }
                            i++;
                            closed = true;
                            break;
                        }
                        literal.Append(sql[i]);
                        i++;
                    }

                    if (!closed)
                    {
                        throw new SqlParseException("Unterminated string literal.", start);
                    }

                    tokens.Add(new Token { Kind = TokenKind.String, Text = literal.ToString(), Position = start });
                    continue;
                }

                if (ch == '"' || ch == '`' || ch == '[')
                {
                    char close = ch == '[' ? ']' : ch;
                    int start = i;
                    i++;
                    StringBuilder id = new StringBuilder();
                    while (i < sql.Length && sql[i] != close)
                    {
                        id.Append(sql[i]);
                        i++;
                    }
                    if (i >= sql.Length)
                    {
                        throw new SqlParseException("Unterminated quoted identifier.", start);
                    }
                    i++;
                    tokens.Add(new Token { Kind = TokenKind.Identifier, Text = id.ToString(), IsKeyword = false, Position = start });
                    continue;
                }

                Token tok = null;
                switch (ch)
                {
                    case ',': tok = new Token { Kind = TokenKind.Comma, Text = ",", Position = i }; break;
                    case '.': tok = new Token { Kind = TokenKind.Dot, Text = ".", Position = i }; break;
                    case '*': tok = new Token { Kind = TokenKind.Star, Text = "*", Position = i }; break;
                    case '+': tok = new Token { Kind = TokenKind.Plus, Text = "+", Position = i }; break;
                    case '-': tok = new Token { Kind = TokenKind.Minus, Text = "-", Position = i }; break;
                    case '/': tok = new Token { Kind = TokenKind.Slash, Text = "/", Position = i }; break;
                    case '%': tok = new Token { Kind = TokenKind.Percent, Text = "%", Position = i }; break;
                    case '(': tok = new Token { Kind = TokenKind.LParen, Text = "(", Position = i }; break;
                    case ')': tok = new Token { Kind = TokenKind.RParen, Text = ")", Position = i }; break;
                    case ';': tok = new Token { Kind = TokenKind.Semicolon, Text = ";", Position = i }; break;
                    case '=': tok = new Token { Kind = TokenKind.Equal, Text = "=", Position = i }; break;
                    case '<':
                        if (i + 1 < sql.Length && sql[i + 1] == '=') { tok = new Token { Kind = TokenKind.LessEqual, Text = "<=", Position = i }; i++; }
                        else if (i + 1 < sql.Length && sql[i + 1] == '>') { tok = new Token { Kind = TokenKind.NotEqual, Text = "<>", Position = i }; i++; }
                        else tok = new Token { Kind = TokenKind.Less, Text = "<", Position = i };
                        break;
                    case '>':
                        if (i + 1 < sql.Length && sql[i + 1] == '=') { tok = new Token { Kind = TokenKind.GreaterEqual, Text = ">=", Position = i }; i++; }
                        else tok = new Token { Kind = TokenKind.Greater, Text = ">", Position = i };
                        break;
                    case '!':
                        if (i + 1 < sql.Length && sql[i + 1] == '=') { tok = new Token { Kind = TokenKind.NotEqual, Text = "!=", Position = i }; i++; }
                        break;
                }

                if (tok == null)
                {
                    throw new SqlParseException("Unexpected character: " + ch, i);
                }

                tokens.Add(tok);
                i++;
            }

            tokens.Add(new Token { Kind = TokenKind.End, Text = "<EOF>", Position = sql.Length });
            return tokens;
        }
    }

    private sealed class SqlParser
    {
        private readonly List<Token> _tokens;
        private int _index;
        private int _exprId;

        public SqlParser(string sql)
        {
            _tokens = SqlTokenizer.Tokenize(sql);
            _index = 0;
            _exprId = 1;
        }

        public QueryNode ParseQuery()
        {
            QueryNode query = new QueryNode();
            query.Ctes = new List<CteNode>();

            if (MatchKeyword("WITH"))
            {
                do
                {
                    Token nameTok = Expect(TokenKind.Identifier, "Expected CTE name.");
                    CteNode cte = new CteNode();
                    cte.Name = nameTok.Text;

                    if (Match(TokenKind.LParen))
                    {
                        while (!Match(TokenKind.RParen))
                        {
                            if (Peek().Kind == TokenKind.End)
                            {
                                throw Error("Unterminated CTE column list.");
                            }
                            Next();
                        }
                    }

                    ExpectKeyword("AS");
                    Expect(TokenKind.LParen, "Expected '(' before CTE query.");
                    cte.Query = ParseQuery();
                    Expect(TokenKind.RParen, "Expected ')' after CTE query.");
                    query.Ctes.Add(cte);
                }
                while (Match(TokenKind.Comma));
            }

            QueryTermNode term = ParseSelectTerm();
            while (IsSetOperator(Peek()))
            {
                Token opTok = Next();
                bool all = false;
                if (opTok.Text.Equals("UNION", StringComparison.OrdinalIgnoreCase) && MatchKeyword("ALL")) all = true;

                SetTermNode set = new SetTermNode();
                set.Left = term;
                set.Right = ParseSelectTerm();
                set.Operator = opTok.Text.ToUpperInvariant();
                set.All = all;
                term = set;
            }
            query.Term = term;

            query.OrderBy = new List<OrderItem>();
            if (MatchKeyword("ORDER"))
            {
                ExpectKeyword("BY");
                do
                {
                    OrderItem item = new OrderItem();
                    item.Expression = ParseExpression();
                    item.Desc = MatchKeyword("DESC");
                    if (!item.Desc) MatchKeyword("ASC");
                    query.OrderBy.Add(item);
                }
                while (Match(TokenKind.Comma));
            }

            if (MatchKeyword("LIMIT")) query.Limit = ParseIntLiteral();
            if (MatchKeyword("OFFSET")) query.Offset = ParseIntLiteral();

            Match(TokenKind.Semicolon);
            if (Peek().Kind != TokenKind.End && Peek().Kind != TokenKind.RParen)
            {
                throw Error("Unexpected token after query: " + Peek().Text);
            }

            return query;
        }

        private QueryTermNode ParseSelectTerm()
        {
            SelectNode select = new SelectNode();
            ExpectKeyword("SELECT");
            select.Distinct = MatchKeyword("DISTINCT");

            select.SelectItems = new List<SelectItemNode>();
            do { select.SelectItems.Add(ParseSelectItem()); } while (Match(TokenKind.Comma));

            if (MatchKeyword("FROM"))
            {
                select.From = new FromNode();
                select.From.Source = ParseTableSource();
                select.From.Joins = new List<JoinNode>();
                while (IsJoinStart(Peek()))
                {
                    select.From.Joins.Add(ParseJoin());
                }
            }

            if (MatchKeyword("WHERE")) select.Where = ParseExpression();

            select.GroupBy = new List<ExprNode>();
            if (MatchKeyword("GROUP"))
            {
                ExpectKeyword("BY");
                do { select.GroupBy.Add(ParseExpression()); } while (Match(TokenKind.Comma));
            }

            if (MatchKeyword("HAVING")) select.Having = ParseExpression();
            return select;
        }
        private SelectItemNode ParseSelectItem()
        {
            if (Match(TokenKind.Star)) return new SelectItemNode { IsWildcard = true };

            if (Peek().Kind == TokenKind.Identifier && Peek(1).Kind == TokenKind.Dot && Peek(2).Kind == TokenKind.Star)
            {
                string qualifier = Next().Text;
                Next();
                Next();
                return new SelectItemNode { IsWildcard = true, WildcardQualifier = qualifier };
            }

            ExprNode expr = ParseExpression();
            string alias = null;

            if (MatchKeyword("AS"))
            {
                alias = ExpectAliasIdentifier();
            }
            else if (Peek().Kind == TokenKind.Identifier && !Peek().IsKeyword)
            {
                Token id = Peek();
                if (!SqlTokenizer.KeywordSet.Contains(id.Text)) alias = Next().Text;
            }

            return new SelectItemNode { Expression = expr, Alias = alias, IsWildcard = false };
        }

        private TableSourceNode ParseTableSource()
        {
            TableSourceNode source;
            if (Match(TokenKind.LParen))
            {
                if (Peek().IsKeyword && (Peek().Text.Equals("SELECT", StringComparison.OrdinalIgnoreCase) || Peek().Text.Equals("WITH", StringComparison.OrdinalIgnoreCase)))
                {
                    SubqueryTableSourceNode sub = new SubqueryTableSourceNode();
                    sub.Query = ParseQuery();
                    Expect(TokenKind.RParen, "Expected ')' after subquery.");
                    source = sub;
                }
                else
                {
                    throw Error("Subquery expected in FROM parenthesis.");
                }
            }
            else
            {
                Token name = Expect(TokenKind.Identifier, "Expected table name.");
                string fullName = name.Text;
                if (Match(TokenKind.Dot))
                {
                    Token second = Expect(TokenKind.Identifier, "Expected table identifier after '.'.");
                    fullName = fullName + "." + second.Text;
                }
                source = new NamedTableSourceNode { Name = fullName };
            }

            if (MatchKeyword("AS")) source.Alias = ExpectAliasIdentifier();
            else if (Peek().Kind == TokenKind.Identifier && !Peek().IsKeyword) source.Alias = Next().Text;
            return source;
        }

        private JoinNode ParseJoin()
        {
            JoinNode join = new JoinNode();
            string joinType = "INNER";

            if (MatchKeyword("LEFT")) { MatchKeyword("OUTER"); joinType = "LEFT"; }
            else if (MatchKeyword("RIGHT")) { MatchKeyword("OUTER"); joinType = "RIGHT"; }
            else if (MatchKeyword("FULL")) { MatchKeyword("OUTER"); joinType = "FULL"; }
            else if (MatchKeyword("CROSS")) { joinType = "CROSS"; }
            else if (MatchKeyword("INNER")) { joinType = "INNER"; }

            ExpectKeyword("JOIN");
            join.JoinType = joinType;
            join.Source = ParseTableSource();

            if (!joinType.Equals("CROSS", StringComparison.OrdinalIgnoreCase))
            {
                ExpectKeyword("ON");
                join.On = ParseExpression();
            }

            return join;
        }

        private ExprNode ParseExpression() { return ParseOr(); }

        private ExprNode ParseOr()
        {
            ExprNode left = ParseAnd();
            while (MatchKeyword("OR"))
            {
                ExprNode right = ParseAnd();
                left = NewExpr(new BinaryExprNode { Operator = "OR", Left = left, Right = right });
            }
            return left;
        }

        private ExprNode ParseAnd()
        {
            ExprNode left = ParseNot();
            while (MatchKeyword("AND"))
            {
                ExprNode right = ParseNot();
                left = NewExpr(new BinaryExprNode { Operator = "AND", Left = left, Right = right });
            }
            return left;
        }

        private ExprNode ParseNot()
        {
            if (MatchKeyword("NOT"))
            {
                return NewExpr(new UnaryExprNode { Operator = "NOT", Operand = ParseNot() });
            }
            return ParseComparison();
        }

        private ExprNode ParseComparison()
        {
            ExprNode left = ParseAdditive();

            if (MatchKeyword("IS"))
            {
                bool negated = MatchKeyword("NOT");
                ExpectKeyword("NULL");
                return NewExpr(new IsNullExprNode { Expression = left, Negated = negated });
            }

            if (MatchKeyword("NOT"))
            {
                if (MatchKeyword("IN")) return ParseIn(left, true);
                if (MatchKeyword("LIKE")) return NewExpr(new LikeExprNode { Input = left, Pattern = ParseAdditive(), Negated = true });
                if (MatchKeyword("BETWEEN"))
                {
                    ExprNode loNeg = ParseAdditive();
                    ExpectKeyword("AND");
                    ExprNode hiNeg = ParseAdditive();
                    return NewExpr(new BetweenExprNode { Input = left, Low = loNeg, High = hiNeg, Negated = true });
                }
                _index--;
            }

            if (MatchKeyword("IN")) return ParseIn(left, false);
            if (MatchKeyword("LIKE")) return NewExpr(new LikeExprNode { Input = left, Pattern = ParseAdditive(), Negated = false });
            if (MatchKeyword("BETWEEN"))
            {
                ExprNode lo = ParseAdditive();
                ExpectKeyword("AND");
                ExprNode hi = ParseAdditive();
                return NewExpr(new BetweenExprNode { Input = left, Low = lo, High = hi, Negated = false });
            }

            if (Match(TokenKind.Equal)) return NewExpr(new BinaryExprNode { Operator = "=", Left = left, Right = ParseAdditive() });
            if (Match(TokenKind.NotEqual)) return NewExpr(new BinaryExprNode { Operator = "<>", Left = left, Right = ParseAdditive() });
            if (Match(TokenKind.Less)) return NewExpr(new BinaryExprNode { Operator = "<", Left = left, Right = ParseAdditive() });
            if (Match(TokenKind.LessEqual)) return NewExpr(new BinaryExprNode { Operator = "<=", Left = left, Right = ParseAdditive() });
            if (Match(TokenKind.Greater)) return NewExpr(new BinaryExprNode { Operator = ">", Left = left, Right = ParseAdditive() });
            if (Match(TokenKind.GreaterEqual)) return NewExpr(new BinaryExprNode { Operator = ">=", Left = left, Right = ParseAdditive() });

            return left;
        }

        private ExprNode ParseIn(ExprNode input, bool negated)
        {
            Expect(TokenKind.LParen, "Expected '(' after IN.");
            if (Peek().IsKeyword && (Peek().Text.Equals("SELECT", StringComparison.OrdinalIgnoreCase) || Peek().Text.Equals("WITH", StringComparison.OrdinalIgnoreCase)))
            {
                QueryNode subquery = ParseQuery();
                Expect(TokenKind.RParen, "Expected ')' after IN subquery.");
                return NewExpr(new InSubqueryExprNode { Input = input, Subquery = subquery, Negated = negated });
            }

            List<ExprNode> list = new List<ExprNode>();
            do { list.Add(ParseExpression()); } while (Match(TokenKind.Comma));
            Expect(TokenKind.RParen, "Expected ')' after IN list.");
            return NewExpr(new InListExprNode { Input = input, Values = list, Negated = negated });
        }

        private ExprNode ParseAdditive()
        {
            ExprNode left = ParseMultiplicative();
            while (true)
            {
                if (Match(TokenKind.Plus)) { left = NewExpr(new BinaryExprNode { Operator = "+", Left = left, Right = ParseMultiplicative() }); continue; }
                if (Match(TokenKind.Minus)) { left = NewExpr(new BinaryExprNode { Operator = "-", Left = left, Right = ParseMultiplicative() }); continue; }
                break;
            }
            return left;
        }

        private ExprNode ParseMultiplicative()
        {
            ExprNode left = ParseUnary();
            while (true)
            {
                if (Match(TokenKind.Star)) { left = NewExpr(new BinaryExprNode { Operator = "*", Left = left, Right = ParseUnary() }); continue; }
                if (Match(TokenKind.Slash)) { left = NewExpr(new BinaryExprNode { Operator = "/", Left = left, Right = ParseUnary() }); continue; }
                if (Match(TokenKind.Percent)) { left = NewExpr(new BinaryExprNode { Operator = "%", Left = left, Right = ParseUnary() }); continue; }
                break;
            }
            return left;
        }

        private ExprNode ParseUnary()
        {
            if (Match(TokenKind.Plus)) return NewExpr(new UnaryExprNode { Operator = "+", Operand = ParseUnary() });
            if (Match(TokenKind.Minus)) return NewExpr(new UnaryExprNode { Operator = "-", Operand = ParseUnary() });
            return ParsePrimary();
        }

        private ExprNode ParsePrimary()
        {
            Token token = Peek();

            if (Match(TokenKind.Number))
            {
                long l;
                double d;
                if (long.TryParse(token.Text, out l)) return NewExpr(new LiteralExprNode { Value = l });
                if (double.TryParse(token.Text, out d)) return NewExpr(new LiteralExprNode { Value = d });
                throw Error("Invalid numeric literal: " + token.Text);
            }

            if (Match(TokenKind.String)) return NewExpr(new LiteralExprNode { Value = token.Text });
            if (MatchKeyword("NULL")) return NewExpr(new LiteralExprNode { Value = null });
            if (MatchKeyword("TRUE")) return NewExpr(new LiteralExprNode { Value = true });
            if (MatchKeyword("FALSE")) return NewExpr(new LiteralExprNode { Value = false });

            if (MatchKeyword("CASE")) return ParseCaseExpression();

            if (Match(TokenKind.LParen))
            {
                if (Peek().IsKeyword && (Peek().Text.Equals("SELECT", StringComparison.OrdinalIgnoreCase) || Peek().Text.Equals("WITH", StringComparison.OrdinalIgnoreCase)))
                {
                    QueryNode query = ParseQuery();
                    Expect(TokenKind.RParen, "Expected ')' after subquery expression.");
                    return NewExpr(new SubqueryExprNode { Query = query });
                }

                ExprNode inner = ParseExpression();
                Expect(TokenKind.RParen, "Expected ')' after expression.");
                return inner;
            }

            if (Match(TokenKind.Identifier))
            {
                string first = token.Text;
                string qualifier = null;
                string name = first;
                if (Match(TokenKind.Dot))
                {
                    Token second = Expect(TokenKind.Identifier, "Expected identifier after '.'.");
                    qualifier = first;
                    name = second.Text;
                }

                if (Match(TokenKind.LParen))
                {
                    FunctionExprNode fn = new FunctionExprNode();
                    fn.Name = name;
                    fn.Arguments = new List<ExprNode>();
                    fn.Distinct = MatchKeyword("DISTINCT");

                    if (!Match(TokenKind.RParen))
                    {
                        do
                        {
                            if (Match(TokenKind.Star)) fn.Arguments.Add(NewExpr(new StarExprNode()));
                            else fn.Arguments.Add(ParseExpression());
                        }
                        while (Match(TokenKind.Comma));
                        Expect(TokenKind.RParen, "Expected ')' after function arguments.");
                    }

                    ExprNode functionNode = NewExpr(fn);
                    if (MatchKeyword("OVER"))
                    {
                        WindowSpecNode spec = ParseWindowSpec();
                        WindowExprNode win = new WindowExprNode();
                        win.Function = fn;
                        win.Spec = spec;
                        return NewExpr(win);
                    }

                    return functionNode;
                }

                return NewExpr(new IdentifierExprNode { Qualifier = qualifier, Name = name });
            }

            throw Error("Unexpected token in expression: " + token.Text);
        }

        private ExprNode ParseCaseExpression()
        {
            CaseExprNode node = new CaseExprNode();
            node.Whens = new List<CaseWhenNode>();

            ExprNode baseExpr = null;
            if (!Peek().IsKeyword || !Peek().Text.Equals("WHEN", StringComparison.OrdinalIgnoreCase))
            {
                if (!(Peek().IsKeyword && Peek().Text.Equals("END", StringComparison.OrdinalIgnoreCase))) baseExpr = ParseExpression();
            }

            while (MatchKeyword("WHEN"))
            {
                ExprNode whenExpr = ParseExpression();
                if (baseExpr != null) whenExpr = NewExpr(new BinaryExprNode { Operator = "=", Left = baseExpr, Right = whenExpr });
                ExpectKeyword("THEN");
                ExprNode thenExpr = ParseExpression();
                node.Whens.Add(new CaseWhenNode { When = whenExpr, Then = thenExpr });
            }

            if (MatchKeyword("ELSE")) node.Else = ParseExpression();
            ExpectKeyword("END");
            return NewExpr(node);
        }

        private WindowSpecNode ParseWindowSpec()
        {
            WindowSpecNode spec = new WindowSpecNode();
            spec.PartitionBy = new List<ExprNode>();
            spec.OrderBy = new List<OrderItem>();

            Expect(TokenKind.LParen, "Expected '(' after OVER.");

            if (MatchKeyword("PARTITION"))
            {
                ExpectKeyword("BY");
                do { spec.PartitionBy.Add(ParseExpression()); } while (Match(TokenKind.Comma));
            }

            if (MatchKeyword("ORDER"))
            {
                ExpectKeyword("BY");
                do
                {
                    OrderItem order = new OrderItem();
                    order.Expression = ParseExpression();
                    order.Desc = MatchKeyword("DESC");
                    if (!order.Desc) MatchKeyword("ASC");
                    spec.OrderBy.Add(order);
                }
                while (Match(TokenKind.Comma));
            }

            Expect(TokenKind.RParen, "Expected ')' after OVER clause.");
            return spec;
        }

        private bool IsJoinStart(Token token)
        {
            if (!token.IsKeyword) return false;
            string t = token.Text.ToUpperInvariant();
            return t == "JOIN" || t == "INNER" || t == "LEFT" || t == "RIGHT" || t == "FULL" || t == "CROSS";
        }

        private bool IsSetOperator(Token token)
        {
            return token.IsKeyword && (token.Text.Equals("UNION", StringComparison.OrdinalIgnoreCase)
                || token.Text.Equals("INTERSECT", StringComparison.OrdinalIgnoreCase)
                || token.Text.Equals("EXCEPT", StringComparison.OrdinalIgnoreCase));
        }

        private int ParseIntLiteral()
        {
            Token tok = Expect(TokenKind.Number, "Expected integer literal.");
            int value;
            if (!int.TryParse(tok.Text, out value)) throw Error("Expected integer literal, found: " + tok.Text);
            return value;
        }

        private string ExpectAliasIdentifier()
        {
            Token tok = Peek();
            if (tok.Kind != TokenKind.Identifier) throw Error("Expected alias identifier.");
            return Next().Text;
        }

        private ExprNode NewExpr(ExprNode node)
        {
            node.Id = _exprId++;
            return node;
        }

        private Token Peek(int offset = 0)
        {
            int pos = _index + offset;
            if (pos >= _tokens.Count) return _tokens[_tokens.Count - 1];
            return _tokens[pos];
        }

        private Token Next()
        {
            Token token = Peek();
            if (_index < _tokens.Count) _index++;
            return token;
        }

        private bool Match(TokenKind kind)
        {
            if (Peek().Kind == kind)
            {
                _index++;
                return true;
            }
            return false;
        }

        private bool MatchKeyword(string keyword)
        {
            Token t = Peek();
            if (t.IsKeyword && t.Text.Equals(keyword, StringComparison.OrdinalIgnoreCase))
            {
                _index++;
                return true;
            }
            return false;
        }

        private Token Expect(TokenKind kind, string message)
        {
            Token t = Peek();
            if (t.Kind != kind) throw Error(message);
            _index++;
            return t;
        }

        private void ExpectKeyword(string keyword)
        {
            if (!MatchKeyword(keyword)) throw Error("Expected keyword " + keyword + ".");
        }

        private SqlParseException Error(string message)
        {
            return new SqlParseException(message, Peek().Position);
        }
    }
    private sealed class QueryExecutor
    {
        private readonly QueryExecutionOptions _options;
        private readonly StringComparer _nameComparer;

        public QueryExecutor(QueryExecutionOptions options)
        {
            _options = options;
            _nameComparer = options.CaseSensitiveIdentifiers ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        }

        public RowSet ExecuteQuery(QueryNode query, Dictionary<string, RowSet> baseTables)
        {
            ExecutionScope scope = new ExecutionScope(_options);
            foreach (KeyValuePair<string, RowSet> kvp in baseTables) scope.Tables[kvp.Key] = kvp.Value;
            return ExecuteQueryWithScope(query, scope);
        }

        private RowSet ExecuteQueryWithScope(QueryNode query, ExecutionScope scope)
        {
            if (query.Ctes != null && query.Ctes.Count > 0)
            {
                for (int i = 0; i < query.Ctes.Count; i++)
                {
                    CteNode cte = query.Ctes[i];
                    RowSet cteResult = ExecuteQueryWithScope(cte.Query, scope.Clone());
                    cteResult.Name = cte.Name;
                    scope.Tables[cte.Name] = cteResult;
                }
            }

            RowSet result = ExecuteTerm(query.Term, scope);

            if (query.OrderBy != null && query.OrderBy.Count > 0) result = ApplyOrderBy(result, query.OrderBy, scope);
            if (query.Offset.HasValue || query.Limit.HasValue) result = ApplyLimitOffset(result, query.Offset.GetValueOrDefault(0), query.Limit);

            return result;
        }

        private RowSet ExecuteTerm(QueryTermNode term, ExecutionScope scope)
        {
            SelectNode select = term as SelectNode;
            if (select != null) return ExecuteSelect(select, scope);

            SetTermNode set = term as SetTermNode;
            if (set != null)
            {
                RowSet left = ExecuteTerm(set.Left, scope.Clone());
                RowSet right = ExecuteTerm(set.Right, scope.Clone());
                return ApplySetOperation(left, right, set.Operator, set.All);
            }

            throw new SqlExecutionException("UNSUPPORTED_SQL_FEATURE", "Unsupported query term.", null);
        }

        private RowSet ExecuteSelect(SelectNode select, ExecutionScope scope)
        {
            RowSet source = BuildSource(select.From, scope);
            source = ApplyWhere(source, select.Where, scope);

            List<SelectItemNode> expanded = ExpandSelectItems(select.SelectItems, source);
            bool hasAggregate = select.GroupBy.Count > 0 || ContainsAggregate(expanded, select.Having);
            bool hasWindow = ContainsWindow(expanded, select.Having);

            if (hasAggregate && hasWindow)
            {
                throw new SqlExecutionException("UNSUPPORTED_SQL_FEATURE", "Window functions combined with grouped aggregates are not supported in the same SELECT.", null);
            }

            RowSet projected = hasAggregate
                ? ExecuteGroupedSelect(source, expanded, select.GroupBy, select.Having, scope)
                : ExecuteRowSelect(source, expanded, scope, hasWindow);

            if (select.Distinct) projected = ApplyDistinct(projected);
            return projected;
        }

        private RowSet BuildSource(FromNode from, ExecutionScope scope)
        {
            if (from == null)
            {
                return new RowSet { Name = "__single", Columns = new List<SqlColumn>(), Rows = new List<object[]> { new object[0] } };
            }

            RowSet current = ResolveTableSource(from.Source, scope);
            for (int i = 0; i < from.Joins.Count; i++)
            {
                JoinNode join = from.Joins[i];
                RowSet right = ResolveTableSource(join.Source, scope);
                current = ApplyJoin(current, right, join, scope);
            }
            return current;
        }

        private RowSet ResolveTableSource(TableSourceNode source, ExecutionScope scope)
        {
            NamedTableSourceNode named = source as NamedTableSourceNode;
            if (named != null)
            {
                RowSet table;
                if (!scope.Tables.TryGetValue(named.Name, out table))
                {
                    throw new SqlValidationException("Unknown table or CTE: " + named.Name);
                }
                RowSet clone = table.Clone();
                string alias = string.IsNullOrWhiteSpace(named.Alias) ? named.Name : named.Alias;
                for (int i = 0; i < clone.Columns.Count; i++) clone.Columns[i].TableAlias = alias;
                clone.Name = alias;
                return clone;
            }

            SubqueryTableSourceNode sub = source as SubqueryTableSourceNode;
            if (sub != null)
            {
                RowSet inner = ExecuteQueryWithScope(sub.Query, scope.Clone());
                string alias = string.IsNullOrWhiteSpace(sub.Alias) ? "__subquery" : sub.Alias;
                for (int i = 0; i < inner.Columns.Count; i++) inner.Columns[i].TableAlias = alias;
                inner.Name = alias;
                return inner;
            }

            throw new SqlExecutionException("UNSUPPORTED_SQL_FEATURE", "Unsupported table source.", null);
        }

        private RowSet ApplyJoin(RowSet left, RowSet right, JoinNode join, ExecutionScope scope)
        {
            RowSet output = new RowSet();
            output.Name = left.Name;
            output.Columns = new List<SqlColumn>(left.Columns.Count + right.Columns.Count);
            output.Columns.AddRange(CloneColumns(left.Columns));
            output.Columns.AddRange(CloneColumns(right.Columns));
            output.Rows = new List<object[]>();

            string joinType = join.JoinType.ToUpperInvariant();
            bool[] rightMatched = new bool[right.Rows.Count];

            for (int i = 0; i < left.Rows.Count; i++)
            {
                object[] leftRow = left.Rows[i];
                bool matchedAny = false;

                for (int j = 0; j < right.Rows.Count; j++)
                {
                    object[] rightRow = right.Rows[j];
                    object[] combined = CombineRows(leftRow, rightRow);

                    bool pass = joinType == "CROSS" || IsTrue(EvaluateExpression(join.On, new EvalContext
                    {
                        RowSet = output,
                        Row = combined,
                        Scope = scope,
                        SourceRows = output.Rows
                    }));

                    if (pass)
                    {
                        matchedAny = true;
                        rightMatched[j] = true;
                        output.Rows.Add(combined);
                    }
                }

                if (!matchedAny && (joinType == "LEFT" || joinType == "FULL"))
                {
                    output.Rows.Add(CombineRows(leftRow, new object[right.Columns.Count]));
                }
            }

            if (joinType == "RIGHT" || joinType == "FULL")
            {
                for (int j = 0; j < right.Rows.Count; j++)
                {
                    if (!rightMatched[j]) output.Rows.Add(CombineRows(new object[left.Columns.Count], right.Rows[j]));
                }
            }

            return output;
        }

        private RowSet ApplyWhere(RowSet source, ExprNode where, ExecutionScope scope)
        {
            if (where == null) return source;

            RowSet filtered = new RowSet();
            filtered.Name = source.Name;
            filtered.Columns = CloneColumns(source.Columns);
            filtered.Rows = new List<object[]>();

            for (int i = 0; i < source.Rows.Count; i++)
            {
                object[] row = source.Rows[i];
                object predicate = EvaluateExpression(where, new EvalContext
                {
                    RowSet = source,
                    Row = row,
                    Scope = scope,
                    SourceRows = source.Rows,
                    RowIndex = i
                });

                if (IsTrue(predicate)) filtered.Rows.Add(row);
            }

            return filtered;
        }

        private RowSet ExecuteGroupedSelect(RowSet source, List<SelectItemNode> selectItems, List<ExprNode> groupBy, ExprNode having, ExecutionScope scope)
        {
            Dictionary<string, GroupBucket> groups = new Dictionary<string, GroupBucket>(StringComparer.Ordinal);

            if (source.Rows.Count == 0 && groupBy.Count == 0)
            {
                groups["__single"] = new GroupBucket { Rows = new List<object[]>() };
            }
            else
            {
                for (int i = 0; i < source.Rows.Count; i++)
                {
                    object[] row = source.Rows[i];
                    string key = BuildGroupKey(source, row, groupBy, scope);
                    GroupBucket bucket;
                    if (!groups.TryGetValue(key, out bucket))
                    {
                        bucket = new GroupBucket { Rows = new List<object[]>() };
                        groups[key] = bucket;
                    }
                    bucket.Rows.Add(row);
                }
            }

            RowSet output = new RowSet();
            output.Name = "__result";
            output.Columns = BuildProjectionColumns(source, selectItems);
            output.Rows = new List<object[]>();

            foreach (KeyValuePair<string, GroupBucket> kvp in groups)
            {
                GroupBucket bucket = kvp.Value;
                object[] representative = bucket.Rows.Count > 0 ? bucket.Rows[0] : new object[source.Columns.Count];

                if (having != null)
                {
                    object hv = EvaluateExpression(having, new EvalContext
                    {
                        RowSet = source,
                        Row = representative,
                        GroupRows = bucket.Rows,
                        Scope = scope,
                        SourceRows = source.Rows
                    });
                    if (!IsTrue(hv)) continue;
                }

                object[] projected = new object[output.Columns.Count];
                for (int i = 0; i < selectItems.Count; i++)
                {
                    projected[i] = EvaluateExpression(selectItems[i].Expression, new EvalContext
                    {
                        RowSet = source,
                        Row = representative,
                        GroupRows = bucket.Rows,
                        Scope = scope,
                        SourceRows = source.Rows
                    });
                }
                output.Rows.Add(projected);
            }

            InferResultTypes(output);
            return output;
        }

        private RowSet ExecuteRowSelect(RowSet source, List<SelectItemNode> selectItems, ExecutionScope scope, bool withWindow)
        {
            RowSet output = new RowSet();
            output.Name = "__result";
            output.Columns = BuildProjectionColumns(source, selectItems);
            output.Rows = new List<object[]>(source.Rows.Count);

            Dictionary<int, object[]> windowValues = null;
            if (withWindow) windowValues = ComputeWindowValues(source, selectItems, scope);

            for (int rowIndex = 0; rowIndex < source.Rows.Count; rowIndex++)
            {
                object[] row = source.Rows[rowIndex];
                object[] projected = new object[selectItems.Count];

                for (int c = 0; c < selectItems.Count; c++)
                {
                    projected[c] = EvaluateExpression(selectItems[c].Expression, new EvalContext
                    {
                        RowSet = source,
                        Row = row,
                        Scope = scope,
                        SourceRows = source.Rows,
                        RowIndex = rowIndex,
                        WindowValues = windowValues
                    });
                }

                output.Rows.Add(projected);
            }

            InferResultTypes(output);
            return output;
        }

        private Dictionary<int, object[]> ComputeWindowValues(RowSet source, List<SelectItemNode> selectItems, ExecutionScope scope)
        {
            List<WindowExprNode> windows = new List<WindowExprNode>();
            for (int i = 0; i < selectItems.Count; i++) CollectWindowExpressions(selectItems[i].Expression, windows);

            Dictionary<int, object[]> map = new Dictionary<int, object[]>();
            for (int i = 0; i < windows.Count; i++)
            {
                WindowExprNode node = windows[i];
                if (map.ContainsKey(node.Id)) continue;
                map[node.Id] = ExecuteWindowFunction(node, source, scope);
            }
            return map;
        }
        private object[] ExecuteWindowFunction(WindowExprNode window, RowSet source, ExecutionScope scope)
        {
            int count = source.Rows.Count;
            object[] output = new object[count];

            Dictionary<string, List<int>> partitions = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int i = 0; i < source.Rows.Count; i++)
            {
                object[] row = source.Rows[i];
                string key = BuildPartitionKey(window.Spec.PartitionBy, source, row, scope, source.Rows, i);
                List<int> indices;
                if (!partitions.TryGetValue(key, out indices))
                {
                    indices = new List<int>();
                    partitions[key] = indices;
                }
                indices.Add(i);
            }

            string fn = window.Function.Name.ToUpperInvariant();

            foreach (KeyValuePair<string, List<int>> kvp in partitions)
            {
                List<int> ordered = kvp.Value;
                if (window.Spec.OrderBy.Count > 0)
                {
                    ordered = ordered.OrderBy(i => i, new WindowRowComparer(this, source, window.Spec.OrderBy, scope, _options.NullsSortFirst)).ToList();
                }

                if (fn == "ROW_NUMBER")
                {
                    for (int pos = 0; pos < ordered.Count; pos++) output[ordered[pos]] = pos + 1;
                    continue;
                }

                if (fn == "RANK" || fn == "DENSE_RANK")
                {
                    int rank = 1;
                    int dense = 1;
                    object[] previousKeys = null;

                    for (int pos = 0; pos < ordered.Count; pos++)
                    {
                        int rowIndex = ordered[pos];
                        object[] currentKeys = BuildOrderKeyTuple(source, source.Rows[rowIndex], window.Spec.OrderBy, scope, source.Rows, rowIndex);
                        bool same = previousKeys != null && AreOrderKeysEqual(previousKeys, currentKeys);

                        if (!same)
                        {
                            if (fn == "RANK") rank = pos + 1;
                            else if (pos > 0) dense++;
                        }

                        output[rowIndex] = fn == "RANK" ? (object)rank : dense;
                        previousKeys = currentKeys;
                    }
                    continue;
                }

                if (fn == "LAG" || fn == "LEAD")
                {
                    if (window.Function.Arguments.Count < 1)
                    {
                        throw new SqlExecutionException("SQL_VALIDATION_ERROR", fn + " requires at least one argument.", null);
                    }

                    int offset = 1;
                    object defaultValue = null;

                    if (window.Function.Arguments.Count >= 2)
                    {
                        object off = EvaluateExpression(window.Function.Arguments[1], new EvalContext
                        {
                            RowSet = source,
                            Row = source.Rows[ordered[0]],
                            Scope = scope,
                            SourceRows = source.Rows,
                            RowIndex = ordered[0]
                        });
                        if (off != null) offset = Convert.ToInt32(ToDouble(off));
                    }

                    if (window.Function.Arguments.Count >= 3)
                    {
                        defaultValue = EvaluateExpression(window.Function.Arguments[2], new EvalContext
                        {
                            RowSet = source,
                            Row = source.Rows[ordered[0]],
                            Scope = scope,
                            SourceRows = source.Rows,
                            RowIndex = ordered[0]
                        });
                    }

                    List<object> valueBuffer = new List<object>(ordered.Count);
                    for (int pos = 0; pos < ordered.Count; pos++)
                    {
                        int rowIndex = ordered[pos];
                        valueBuffer.Add(EvaluateExpression(window.Function.Arguments[0], new EvalContext
                        {
                            RowSet = source,
                            Row = source.Rows[rowIndex],
                            Scope = scope,
                            SourceRows = source.Rows,
                            RowIndex = rowIndex
                        }));
                    }

                    for (int pos = 0; pos < ordered.Count; pos++)
                    {
                        int targetPos = fn == "LAG" ? pos - offset : pos + offset;
                        int rowIndex = ordered[pos];
                        output[rowIndex] = (targetPos >= 0 && targetPos < valueBuffer.Count) ? valueBuffer[targetPos] : defaultValue;
                    }
                    continue;
                }

                if (IsAggregateFunction(fn))
                {
                    List<object[]> partitionRows = new List<object[]>();
                    for (int p = 0; p < ordered.Count; p++) partitionRows.Add(source.Rows[ordered[p]]);

                    object aggregate = EvaluateAggregateFunction(window.Function, partitionRows, source, scope);
                    for (int pos = 0; pos < ordered.Count; pos++) output[ordered[pos]] = aggregate;
                    continue;
                }

                throw new SqlExecutionException("UNSUPPORTED_SQL_FEATURE", "Unsupported window function: " + window.Function.Name, null);
            }

            return output;
        }

        private RowSet ApplySetOperation(RowSet left, RowSet right, string op, bool all)
        {
            if (left.Columns.Count != right.Columns.Count)
            {
                throw new SqlValidationException("Set operators require same number of columns on both sides.");
            }

            RowSet output = new RowSet();
            output.Name = "__set";
            output.Columns = CloneColumns(left.Columns);
            output.Rows = new List<object[]>();

            if (op == "UNION")
            {
                if (all)
                {
                    output.Rows.AddRange(CloneRows(left.Rows));
                    output.Rows.AddRange(CloneRows(right.Rows));
                }
                else
                {
                    HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
                    AddDistinctRows(output.Rows, left.Rows, keys);
                    AddDistinctRows(output.Rows, right.Rows, keys);
                }
            }
            else if (op == "INTERSECT")
            {
                HashSet<string> rightKeys = new HashSet<string>(right.Rows.Select(BuildRowKey), StringComparer.Ordinal);
                HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < left.Rows.Count; i++)
                {
                    string key = BuildRowKey(left.Rows[i]);
                    if (rightKeys.Contains(key) && seen.Add(key)) output.Rows.Add(CloneRow(left.Rows[i]));
                }
            }
            else if (op == "EXCEPT")
            {
                HashSet<string> rightKeys = new HashSet<string>(right.Rows.Select(BuildRowKey), StringComparer.Ordinal);
                HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < left.Rows.Count; i++)
                {
                    string key = BuildRowKey(left.Rows[i]);
                    if (!rightKeys.Contains(key) && (all || seen.Add(key))) output.Rows.Add(CloneRow(left.Rows[i]));
                }
            }
            else
            {
                throw new SqlExecutionException("UNSUPPORTED_SQL_FEATURE", "Unsupported set operator: " + op, null);
            }

            InferResultTypes(output);
            return output;
        }

        private static void AddDistinctRows(List<object[]> destination, List<object[]> source, HashSet<string> keys)
        {
            for (int i = 0; i < source.Count; i++)
            {
                string key = BuildRowKey(source[i]);
                if (keys.Add(key)) destination.Add(CloneRow(source[i]));
            }
        }

        private RowSet ApplyDistinct(RowSet input)
        {
            RowSet output = new RowSet();
            output.Name = input.Name;
            output.Columns = CloneColumns(input.Columns);
            output.Rows = new List<object[]>();

            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < input.Rows.Count; i++)
            {
                string key = BuildRowKey(input.Rows[i]);
                if (keys.Add(key)) output.Rows.Add(input.Rows[i]);
            }
            return output;
        }

        private RowSet ApplyOrderBy(RowSet input, List<OrderItem> orderBy, ExecutionScope scope)
        {
            List<int> indices = Enumerable.Range(0, input.Rows.Count).ToList();
            indices.Sort(new RowIndexComparer(this, input, orderBy, scope, _options.NullsSortFirst));

            RowSet output = new RowSet();
            output.Name = input.Name;
            output.Columns = CloneColumns(input.Columns);
            output.Rows = new List<object[]>(indices.Count);
            for (int i = 0; i < indices.Count; i++) output.Rows.Add(input.Rows[indices[i]]);
            return output;
        }

        private RowSet ApplyLimitOffset(RowSet input, int offset, int? limit)
        {
            offset = Math.Max(0, offset);
            int start = Math.Min(offset, input.Rows.Count);
            int count = limit.HasValue ? Math.Max(0, Math.Min(limit.Value, input.Rows.Count - start)) : input.Rows.Count - start;

            RowSet output = new RowSet();
            output.Name = input.Name;
            output.Columns = CloneColumns(input.Columns);
            output.Rows = new List<object[]>(count);
            for (int i = 0; i < count; i++) output.Rows.Add(input.Rows[start + i]);
            return output;
        }

        private List<SelectItemNode> ExpandSelectItems(List<SelectItemNode> items, RowSet source)
        {
            List<SelectItemNode> expanded = new List<SelectItemNode>();
            for (int i = 0; i < items.Count; i++)
            {
                SelectItemNode item = items[i];
                if (!item.IsWildcard)
                {
                    expanded.Add(item);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(item.WildcardQualifier))
                {
                    for (int c = 0; c < source.Columns.Count; c++)
                    {
                        SqlColumn column = source.Columns[c];
                        expanded.Add(new SelectItemNode
                        {
                            IsWildcard = false,
                            Alias = column.Name,
                            Expression = new IdentifierExprNode { Id = 0, Qualifier = column.TableAlias, Name = column.Name }
                        });
                    }
                }
                else
                {
                    bool found = false;
                    for (int c = 0; c < source.Columns.Count; c++)
                    {
                        SqlColumn column = source.Columns[c];
                        if (_nameComparer.Equals(column.TableAlias, item.WildcardQualifier))
                        {
                            expanded.Add(new SelectItemNode
                            {
                                IsWildcard = false,
                                Alias = column.Name,
                                Expression = new IdentifierExprNode { Id = 0, Qualifier = column.TableAlias, Name = column.Name }
                            });
                            found = true;
                        }
                    }
                    if (!found) throw new SqlValidationException("Unknown wildcard qualifier: " + item.WildcardQualifier);
                }
            }
            return expanded;
        }

        private List<SqlColumn> BuildProjectionColumns(RowSet source, List<SelectItemNode> items)
        {
            List<SqlColumn> columns = new List<SqlColumn>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                string alias = items[i].Alias;
                if (string.IsNullOrWhiteSpace(alias)) alias = GetExpressionAlias(items[i].Expression, i + 1);
                columns.Add(new SqlColumn { Name = alias, TableAlias = "__result", DataType = SqlDataType.String, Nullable = true });
            }
            return columns;
        }

        private static string GetExpressionAlias(ExprNode expr, int ordinal)
        {
            IdentifierExprNode id = expr as IdentifierExprNode;
            if (id != null) return id.Name;

            FunctionExprNode fn = expr as FunctionExprNode;
            if (fn != null) return fn.Name.ToLowerInvariant();

            WindowExprNode win = expr as WindowExprNode;
            if (win != null) return win.Function.Name.ToLowerInvariant();

            return "expr" + ordinal;
        }
        private bool ContainsAggregate(List<SelectItemNode> items, ExprNode having)
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (ContainsAggregate(items[i].Expression)) return true;
            }
            return having != null && ContainsAggregate(having);
        }

        private bool ContainsAggregate(ExprNode expr)
        {
            if (expr == null) return false;

            FunctionExprNode fn = expr as FunctionExprNode;
            if (fn != null) return IsAggregateFunction(fn.Name);

            if (expr is WindowExprNode) return false;

            BinaryExprNode bin = expr as BinaryExprNode;
            if (bin != null) return ContainsAggregate(bin.Left) || ContainsAggregate(bin.Right);

            UnaryExprNode un = expr as UnaryExprNode;
            if (un != null) return ContainsAggregate(un.Operand);

            CaseExprNode cas = expr as CaseExprNode;
            if (cas != null)
            {
                for (int i = 0; i < cas.Whens.Count; i++)
                {
                    if (ContainsAggregate(cas.Whens[i].When) || ContainsAggregate(cas.Whens[i].Then)) return true;
                }
                return ContainsAggregate(cas.Else);
            }

            InListExprNode inList = expr as InListExprNode;
            if (inList != null)
            {
                if (ContainsAggregate(inList.Input)) return true;
                for (int i = 0; i < inList.Values.Count; i++) if (ContainsAggregate(inList.Values[i])) return true;
            }

            IsNullExprNode isn = expr as IsNullExprNode;
            if (isn != null) return ContainsAggregate(isn.Expression);

            LikeExprNode like = expr as LikeExprNode;
            if (like != null) return ContainsAggregate(like.Input) || ContainsAggregate(like.Pattern);

            BetweenExprNode between = expr as BetweenExprNode;
            if (between != null) return ContainsAggregate(between.Input) || ContainsAggregate(between.Low) || ContainsAggregate(between.High);

            InSubqueryExprNode inSub = expr as InSubqueryExprNode;
            if (inSub != null) return ContainsAggregate(inSub.Input);

            return false;
        }

        private bool ContainsWindow(List<SelectItemNode> items, ExprNode having)
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (ContainsWindow(items[i].Expression)) return true;
            }
            return having != null && ContainsWindow(having);
        }

        private bool ContainsWindow(ExprNode expr)
        {
            if (expr == null) return false;
            if (expr is WindowExprNode) return true;

            BinaryExprNode bin = expr as BinaryExprNode;
            if (bin != null) return ContainsWindow(bin.Left) || ContainsWindow(bin.Right);

            UnaryExprNode un = expr as UnaryExprNode;
            if (un != null) return ContainsWindow(un.Operand);

            FunctionExprNode fn = expr as FunctionExprNode;
            if (fn != null)
            {
                for (int i = 0; i < fn.Arguments.Count; i++) if (ContainsWindow(fn.Arguments[i])) return true;
            }

            CaseExprNode c = expr as CaseExprNode;
            if (c != null)
            {
                for (int i = 0; i < c.Whens.Count; i++) if (ContainsWindow(c.Whens[i].When) || ContainsWindow(c.Whens[i].Then)) return true;
                return ContainsWindow(c.Else);
            }

            InListExprNode inList = expr as InListExprNode;
            if (inList != null)
            {
                if (ContainsWindow(inList.Input)) return true;
                for (int i = 0; i < inList.Values.Count; i++) if (ContainsWindow(inList.Values[i])) return true;
            }

            return false;
        }

        private static void CollectWindowExpressions(ExprNode expr, List<WindowExprNode> output)
        {
            if (expr == null) return;

            WindowExprNode win = expr as WindowExprNode;
            if (win != null)
            {
                output.Add(win);
                return;
            }

            BinaryExprNode bin = expr as BinaryExprNode;
            if (bin != null)
            {
                CollectWindowExpressions(bin.Left, output);
                CollectWindowExpressions(bin.Right, output);
                return;
            }

            UnaryExprNode un = expr as UnaryExprNode;
            if (un != null)
            {
                CollectWindowExpressions(un.Operand, output);
                return;
            }

            FunctionExprNode fn = expr as FunctionExprNode;
            if (fn != null)
            {
                for (int i = 0; i < fn.Arguments.Count; i++) CollectWindowExpressions(fn.Arguments[i], output);
                return;
            }

            CaseExprNode c = expr as CaseExprNode;
            if (c != null)
            {
                for (int i = 0; i < c.Whens.Count; i++)
                {
                    CollectWindowExpressions(c.Whens[i].When, output);
                    CollectWindowExpressions(c.Whens[i].Then, output);
                }
                CollectWindowExpressions(c.Else, output);
                return;
            }

            InListExprNode inList = expr as InListExprNode;
            if (inList != null)
            {
                CollectWindowExpressions(inList.Input, output);
                for (int i = 0; i < inList.Values.Count; i++) CollectWindowExpressions(inList.Values[i], output);
            }
        }

        private object EvaluateExpression(ExprNode expr, EvalContext context)
        {
            if (expr == null) return null;

            LiteralExprNode literal = expr as LiteralExprNode;
            if (literal != null) return literal.Value;

            IdentifierExprNode id = expr as IdentifierExprNode;
            if (id != null) return ResolveIdentifier(context.RowSet, context.Row, id.Qualifier, id.Name);

            UnaryExprNode unary = expr as UnaryExprNode;
            if (unary != null)
            {
                object value = EvaluateExpression(unary.Operand, context);
                if (unary.Operator == "NOT")
                {
                    if (value == null) return null;
                    return !IsTrue(value);
                }
                if (unary.Operator == "-") return Negate(value);
                if (unary.Operator == "+") return ToNumeric(value);
            }

            BinaryExprNode binary = expr as BinaryExprNode;
            if (binary != null)
            {
                object left = EvaluateExpression(binary.Left, context);
                object right = EvaluateExpression(binary.Right, context);
                return EvaluateBinary(binary.Operator, left, right);
            }

            FunctionExprNode fn = expr as FunctionExprNode;
            if (fn != null)
            {
                if (IsAggregateFunction(fn.Name))
                {
                    if (context.GroupRows == null) throw new SqlExecutionException("SQL_VALIDATION_ERROR", "Aggregate function used without GROUP BY context: " + fn.Name, null);
                    return EvaluateAggregateFunction(fn, context.GroupRows, context.RowSet, context.Scope);
                }
                return EvaluateScalarFunction(fn, context);
            }

            WindowExprNode window = expr as WindowExprNode;
            if (window != null)
            {
                if (context.WindowValues == null) throw new SqlExecutionException("SQL_VALIDATION_ERROR", "Window context unavailable during expression evaluation.", null);
                object[] values;
                if (!context.WindowValues.TryGetValue(window.Id, out values)) throw new SqlExecutionException("SQL_VALIDATION_ERROR", "Window result not found for expression.", null);
                return values[context.RowIndex];
            }

            IsNullExprNode isNull = expr as IsNullExprNode;
            if (isNull != null)
            {
                bool isNullValue = EvaluateExpression(isNull.Expression, context) == null;
                return isNull.Negated ? !isNullValue : isNullValue;
            }

            LikeExprNode like = expr as LikeExprNode;
            if (like != null)
            {
                object input = EvaluateExpression(like.Input, context);
                object pattern = EvaluateExpression(like.Pattern, context);
                bool matched = LikeMatch(input == null ? null : input.ToString(), pattern == null ? null : pattern.ToString());
                return like.Negated ? !matched : matched;
            }

            BetweenExprNode between = expr as BetweenExprNode;
            if (between != null)
            {
                object val = EvaluateExpression(between.Input, context);
                object lo = EvaluateExpression(between.Low, context);
                object hi = EvaluateExpression(between.High, context);
                bool inRange = CompareSqlValues(val, lo, _options.NullsSortFirst) >= 0 && CompareSqlValues(val, hi, _options.NullsSortFirst) <= 0;
                return between.Negated ? !inRange : inRange;
            }

            InListExprNode inListExpr = expr as InListExprNode;
            if (inListExpr != null)
            {
                object input = EvaluateExpression(inListExpr.Input, context);
                bool found = false;
                for (int i = 0; i < inListExpr.Values.Count; i++)
                {
                    object item = EvaluateExpression(inListExpr.Values[i], context);
                    if (CompareSqlValues(input, item, _options.NullsSortFirst) == 0)
                    {
                        found = true;
                        break;
                    }
                }
                return inListExpr.Negated ? !found : found;
            }

            InSubqueryExprNode inSub = expr as InSubqueryExprNode;
            if (inSub != null)
            {
                object input = EvaluateExpression(inSub.Input, context);
                RowSet subResult = ExecuteQueryWithScope(inSub.Subquery, context.Scope.Clone());
                bool found = false;
                for (int r = 0; r < subResult.Rows.Count; r++)
                {
                    if (subResult.Columns.Count == 0) break;
                    if (CompareSqlValues(input, subResult.Rows[r][0], _options.NullsSortFirst) == 0)
                    {
                        found = true;
                        break;
                    }
                }
                return inSub.Negated ? !found : found;
            }

            SubqueryExprNode subExpr = expr as SubqueryExprNode;
            if (subExpr != null)
            {
                RowSet rs = ExecuteQueryWithScope(subExpr.Query, context.Scope.Clone());
                if (rs.Rows.Count == 0 || rs.Columns.Count == 0) return null;
                return rs.Rows[0][0];
            }

            CaseExprNode caseExpr = expr as CaseExprNode;
            if (caseExpr != null)
            {
                for (int i = 0; i < caseExpr.Whens.Count; i++)
                {
                    CaseWhenNode when = caseExpr.Whens[i];
                    if (IsTrue(EvaluateExpression(when.When, context))) return EvaluateExpression(when.Then, context);
                }
                return EvaluateExpression(caseExpr.Else, context);
            }

            if (expr is StarExprNode) return 1;

            throw new SqlExecutionException("UNSUPPORTED_SQL_FEATURE", "Unsupported expression node.", null);
        }
        private object EvaluateScalarFunction(FunctionExprNode fn, EvalContext context)
        {
            string name = fn.Name.ToUpperInvariant();
            List<object> args = new List<object>(fn.Arguments.Count);
            for (int i = 0; i < fn.Arguments.Count; i++) args.Add(EvaluateExpression(fn.Arguments[i], context));

            switch (name)
            {
                case "LOWER": return args.Count > 0 && args[0] != null ? args[0].ToString().ToLowerInvariant() : null;
                case "UPPER": return args.Count > 0 && args[0] != null ? args[0].ToString().ToUpperInvariant() : null;
                case "TRIM": return args.Count > 0 && args[0] != null ? args[0].ToString().Trim() : null;
                case "LTRIM": return args.Count > 0 && args[0] != null ? args[0].ToString().TrimStart() : null;
                case "RTRIM": return args.Count > 0 && args[0] != null ? args[0].ToString().TrimEnd() : null;
                case "LENGTH":
                case "LEN": return args.Count > 0 && args[0] != null ? args[0].ToString().Length : (object)null;
                case "SUBSTR":
                case "SUBSTRING": return EvalSubstring(args);
                case "REPLACE": return args.Count >= 3 && args[0] != null ? args[0].ToString().Replace(args[1] == null ? string.Empty : args[1].ToString(), args[2] == null ? string.Empty : args[2].ToString()) : null;
                case "CONCAT": return string.Concat(args.Select(a => a == null ? string.Empty : a.ToString()));
                case "ABS": return Abs(args.Count > 0 ? args[0] : null);
                case "ROUND": return Round(args);
                case "CEIL":
                case "CEILING": return Ceiling(args.Count > 0 ? args[0] : null);
                case "FLOOR": return Floor(args.Count > 0 ? args[0] : null);
                case "POWER": return Pow(args);
                case "COALESCE": return args.FirstOrDefault(a => a != null);
                case "NULLIF":
                    if (args.Count < 2) return null;
                    return CompareSqlValues(args[0], args[1], _options.NullsSortFirst) == 0 ? null : args[0];
                case "DATE": return ParseDateLike(args.Count > 0 ? args[0] : null, false);
                case "DATETIME": return ParseDateLike(args.Count > 0 ? args[0] : null, true);
                case "STRFTIME": return EvalStrftime(args);
                default: throw new SqlExecutionException("UNSUPPORTED_SQL_FEATURE", "Unsupported scalar function: " + fn.Name, null);
            }
        }

        private object EvaluateAggregateFunction(FunctionExprNode fn, List<object[]> rows, RowSet rowSet, ExecutionScope scope)
        {
            string name = fn.Name.ToUpperInvariant();
            bool star = fn.Arguments.Count == 1 && fn.Arguments[0] is StarExprNode;

            if (name == "COUNT")
            {
                if (star || fn.Arguments.Count == 0) return rows.Count;

                HashSet<string> distinct = fn.Distinct ? new HashSet<string>(StringComparer.Ordinal) : null;
                int count = 0;
                for (int i = 0; i < rows.Count; i++)
                {
                    object value = EvaluateExpression(fn.Arguments[0], new EvalContext { RowSet = rowSet, Row = rows[i], Scope = scope, SourceRows = rows });
                    if (value == null) continue;
                    if (distinct != null)
                    {
                        string key = BuildValueKey(value);
                        if (distinct.Add(key)) count++;
                    }
                    else count++;
                }
                return count;
            }

            if (name == "SUM" || name == "AVG" || name == "MIN" || name == "MAX")
            {
                List<object> values = new List<object>();
                for (int i = 0; i < rows.Count; i++)
                {
                    object value = EvaluateExpression(fn.Arguments[0], new EvalContext { RowSet = rowSet, Row = rows[i], Scope = scope, SourceRows = rows });
                    if (value != null) values.Add(value);
                }
                if (values.Count == 0) return null;

                if (fn.Distinct)
                {
                    HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                    values = values.Where(v => seen.Add(BuildValueKey(v))).ToList();
                }

                if (name == "SUM") return values.Sum(ToDouble);
                if (name == "AVG") return values.Sum(ToDouble) / values.Count;
                if (name == "MIN") return values.Aggregate((x, y) => CompareSqlValues(x, y, _options.NullsSortFirst) <= 0 ? x : y);
                if (name == "MAX") return values.Aggregate((x, y) => CompareSqlValues(x, y, _options.NullsSortFirst) >= 0 ? x : y);
            }

            throw new SqlExecutionException("UNSUPPORTED_SQL_FEATURE", "Unsupported aggregate function: " + fn.Name, null);
        }

        private static bool IsAggregateFunction(string name)
        {
            string n = name.ToUpperInvariant();
            return n == "COUNT" || n == "SUM" || n == "AVG" || n == "MIN" || n == "MAX";
        }

        private object ResolveIdentifier(RowSet rowSet, object[] row, string qualifier, string name)
        {
            int matchedIndex = -1;
            for (int i = 0; i < rowSet.Columns.Count; i++)
            {
                SqlColumn col = rowSet.Columns[i];
                bool qualifierMatch = string.IsNullOrWhiteSpace(qualifier) || _nameComparer.Equals(col.TableAlias, qualifier);
                bool nameMatch = _nameComparer.Equals(col.Name, name);
                if (qualifierMatch && nameMatch)
                {
                    if (matchedIndex >= 0 && string.IsNullOrWhiteSpace(qualifier)) throw new SqlValidationException("Ambiguous column reference: " + name);
                    matchedIndex = i;
                }
            }

            if (matchedIndex < 0) throw new SqlValidationException("Unknown column reference: " + (string.IsNullOrWhiteSpace(qualifier) ? name : qualifier + "." + name));
            return row[matchedIndex];
        }

        private object EvaluateBinary(string op, object left, object right)
        {
            switch (op)
            {
                case "AND": return SqlAnd(left, right);
                case "OR": return SqlOr(left, right);
                case "=": if (left == null || right == null) return null; return CompareSqlValues(left, right, _options.NullsSortFirst) == 0;
                case "<>": if (left == null || right == null) return null; return CompareSqlValues(left, right, _options.NullsSortFirst) != 0;
                case "<": if (left == null || right == null) return null; return CompareSqlValues(left, right, _options.NullsSortFirst) < 0;
                case "<=": if (left == null || right == null) return null; return CompareSqlValues(left, right, _options.NullsSortFirst) <= 0;
                case ">": if (left == null || right == null) return null; return CompareSqlValues(left, right, _options.NullsSortFirst) > 0;
                case ">=": if (left == null || right == null) return null; return CompareSqlValues(left, right, _options.NullsSortFirst) >= 0;
                case "+": return Add(left, right);
                case "-": return Subtract(left, right);
                case "*": return Multiply(left, right);
                case "/": return Divide(left, right);
                case "%": return Modulo(left, right);
                default: throw new SqlExecutionException("UNSUPPORTED_SQL_FEATURE", "Unsupported binary operator: " + op, null);
            }
        }

        private static object SqlAnd(object left, object right)
        {
            bool? l = ToSqlBool(left);
            bool? r = ToSqlBool(right);
            if (l == false || r == false) return false;
            if (l == null || r == null) return null;
            return true;
        }

        private static object SqlOr(object left, object right)
        {
            bool? l = ToSqlBool(left);
            bool? r = ToSqlBool(right);
            if (l == true || r == true) return true;
            if (l == null || r == null) return null;
            return false;
        }

        private static bool? ToSqlBool(object value)
        {
            if (value == null) return null;
            if (value is bool) return (bool)value;
            bool parsed;
            if (bool.TryParse(value.ToString(), out parsed)) return parsed;
            if (value.ToString() == "1") return true;
            if (value.ToString() == "0") return false;
            return null;
        }

        private static bool IsTrue(object value)
        {
            bool? b = ToSqlBool(value);
            return b.HasValue && b.Value;
        }

        private static object Add(object a, object b)
        {
            if (a == null || b == null) return null;
            if (a is string || b is string) return a.ToString() + b.ToString();
            return ToDouble(a) + ToDouble(b);
        }

        private static object Subtract(object a, object b) { if (a == null || b == null) return null; return ToDouble(a) - ToDouble(b); }
        private static object Multiply(object a, object b) { if (a == null || b == null) return null; return ToDouble(a) * ToDouble(b); }
        private static object Divide(object a, object b) { if (a == null || b == null) return null; double divisor = ToDouble(b); if (Math.Abs(divisor) < 1e-15) return null; return ToDouble(a) / divisor; }
        private static object Modulo(object a, object b) { if (a == null || b == null) return null; double divisor = ToDouble(b); if (Math.Abs(divisor) < 1e-15) return null; return ToDouble(a) % divisor; }
        private static object Negate(object value) { if (value == null) return null; return -ToDouble(value); }
        private static object ToNumeric(object value) { if (value == null) return null; return ToDouble(value); }
        private static object EvalSubstring(List<object> args)
        {
            if (args.Count < 2 || args[0] == null || args[1] == null) return null;
            string text = args[0].ToString();
            int start = Convert.ToInt32(ToDouble(args[1])) - 1;
            if (start < 0) start = 0;
            if (start >= text.Length) return string.Empty;
            if (args.Count < 3 || args[2] == null) return text.Substring(start);
            int len = Convert.ToInt32(ToDouble(args[2]));
            if (len < 0) len = 0;
            if (start + len > text.Length) len = text.Length - start;
            return text.Substring(start, len);
        }

        private static object Abs(object value) { if (value == null) return null; return Math.Abs(ToDouble(value)); }

        private static object Round(List<object> args)
        {
            if (args.Count == 0 || args[0] == null) return null;
            int digits = 0;
            if (args.Count > 1 && args[1] != null) digits = Convert.ToInt32(ToDouble(args[1]));
            return Math.Round(ToDouble(args[0]), digits);
        }

        private static object Ceiling(object value) { if (value == null) return null; return Math.Ceiling(ToDouble(value)); }
        private static object Floor(object value) { if (value == null) return null; return Math.Floor(ToDouble(value)); }
        private static object Pow(List<object> args) { if (args.Count < 2 || args[0] == null || args[1] == null) return null; return Math.Pow(ToDouble(args[0]), ToDouble(args[1])); }

        private static object ParseDateLike(object value, bool keepTime)
        {
            if (value == null) return null;
            DateTime parsed;
            if (!DateTime.TryParse(value.ToString(), out parsed)) return null;
            return keepTime ? (object)parsed : parsed.Date;
        }

        private static object EvalStrftime(List<object> args)
        {
            if (args.Count < 2 || args[0] == null || args[1] == null) return null;
            DateTime dt;
            if (!DateTime.TryParse(args[1].ToString(), out dt)) return null;
            string fmt = args[0].ToString();
            string netFmt = fmt.Replace("%Y", "yyyy").Replace("%m", "MM").Replace("%d", "dd").Replace("%H", "HH").Replace("%M", "mm").Replace("%S", "ss");
            return dt.ToString(netFmt);
        }

        private static bool LikeMatch(string input, string pattern)
        {
            if (input == null || pattern == null) return false;
            string regex = "^" + Regex.Escape(pattern).Replace("\\%", ".*").Replace("\\_", ".") + "$";
            return Regex.IsMatch(input, regex, RegexOptions.Singleline);
        }

        private static double ToDouble(object value)
        {
            if (value == null) return 0d;
            if (value is double) return (double)value;
            if (value is float) return (float)value;
            if (value is decimal) return Convert.ToDouble((decimal)value);
            if (value is long) return (long)value;
            if (value is int) return (int)value;
            if (value is bool) return (bool)value ? 1d : 0d;
            double parsed;
            if (double.TryParse(value.ToString(), out parsed)) return parsed;
            return 0d;
        }

        private static int CompareSqlValues(object left, object right, bool nullsFirst)
        {
            if (left == null && right == null) return 0;
            if (left == null) return nullsFirst ? -1 : 1;
            if (right == null) return nullsFirst ? 1 : -1;

            DateTime ldt;
            DateTime rdt;
            if (DateTime.TryParse(left.ToString(), out ldt) && DateTime.TryParse(right.ToString(), out rdt)) return ldt.CompareTo(rdt);

            double ln;
            double rn;
            if (double.TryParse(left.ToString(), out ln) && double.TryParse(right.ToString(), out rn)) return ln.CompareTo(rn);

            bool lb;
            bool rb;
            if (bool.TryParse(left.ToString(), out lb) && bool.TryParse(right.ToString(), out rb)) return lb.CompareTo(rb);

            return string.Compare(left.ToString(), right.ToString(), StringComparison.Ordinal);
        }

        private string BuildGroupKey(RowSet rowSet, object[] row, List<ExprNode> groupBy, ExecutionScope scope)
        {
            if (groupBy == null || groupBy.Count == 0) return "__all";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < groupBy.Count; i++)
            {
                object value = EvaluateExpression(groupBy[i], new EvalContext { RowSet = rowSet, Row = row, Scope = scope, SourceRows = rowSet.Rows });
                if (i > 0) sb.Append('|');
                sb.Append(BuildValueKey(value));
            }
            return sb.ToString();
        }

        private string BuildPartitionKey(List<ExprNode> exprs, RowSet rowSet, object[] row, ExecutionScope scope, List<object[]> sourceRows, int rowIndex)
        {
            if (exprs == null || exprs.Count == 0) return "__all";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < exprs.Count; i++)
            {
                if (i > 0) sb.Append('|');
                object value = EvaluateExpression(exprs[i], new EvalContext { RowSet = rowSet, Row = row, Scope = scope, SourceRows = sourceRows, RowIndex = rowIndex });
                sb.Append(BuildValueKey(value));
            }
            return sb.ToString();
        }

        private object[] BuildOrderKeyTuple(RowSet rowSet, object[] row, List<OrderItem> orderBy, ExecutionScope scope, List<object[]> sourceRows, int rowIndex)
        {
            object[] key = new object[orderBy.Count];
            for (int i = 0; i < orderBy.Count; i++) key[i] = EvaluateExpression(orderBy[i].Expression, new EvalContext { RowSet = rowSet, Row = row, Scope = scope, SourceRows = sourceRows, RowIndex = rowIndex });
            return key;
        }

        private static bool AreOrderKeysEqual(object[] a, object[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (BuildValueKey(a[i]) != BuildValueKey(b[i])) return false;
            return true;
        }

        private static string BuildValueKey(object value)
        {
            if (value == null) return "N:";
            if (value is DateTime) return "D:" + ((DateTime)value).ToString("o");
            return value.GetType().Name + ":" + value.ToString();
        }

        private static string BuildRowKey(object[] row)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < row.Length; i++)
            {
                if (i > 0) sb.Append('\u001F');
                sb.Append(BuildValueKey(row[i]));
            }
            return sb.ToString();
        }

        private static object[] CloneRow(object[] row)
        {
            object[] copy = new object[row.Length];
            Array.Copy(row, copy, row.Length);
            return copy;
        }

        private static List<object[]> CloneRows(List<object[]> rows)
        {
            List<object[]> copies = new List<object[]>(rows.Count);
            for (int i = 0; i < rows.Count; i++) copies.Add(CloneRow(rows[i]));
            return copies;
        }

        private static object[] CombineRows(object[] left, object[] right)
        {
            object[] combined = new object[left.Length + right.Length];
            Array.Copy(left, 0, combined, 0, left.Length);
            Array.Copy(right, 0, combined, left.Length, right.Length);
            return combined;
        }

        private static List<SqlColumn> CloneColumns(List<SqlColumn> columns)
        {
            List<SqlColumn> clone = new List<SqlColumn>(columns.Count);
            for (int i = 0; i < columns.Count; i++) clone.Add(new SqlColumn { Name = columns[i].Name, TableAlias = columns[i].TableAlias, DataType = columns[i].DataType, Nullable = columns[i].Nullable });
            return clone;
        }

        private static void InferResultTypes(RowSet rowSet)
        {
            for (int c = 0; c < rowSet.Columns.Count; c++)
            {
                SqlDataType inferred = SqlDataType.Null;
                bool nullable = false;
                for (int r = 0; r < rowSet.Rows.Count; r++)
                {
                    object v = rowSet.Rows[r][c];
                    if (v == null)
                    {
                        nullable = true;
                        continue;
                    }
                    inferred = InferTypeFromValue(v, inferred);
                }
                rowSet.Columns[c].DataType = inferred;
                rowSet.Columns[c].Nullable = nullable || inferred == SqlDataType.Null;
            }
        }

        private static SqlDataType InferTypeFromValue(object value, SqlDataType current)
        {
            SqlDataType incoming;
            if (value is bool) incoming = SqlDataType.Boolean;
            else if (value is int || value is long) incoming = SqlDataType.Int64;
            else if (value is float || value is double) incoming = SqlDataType.Double;
            else if (value is decimal) incoming = SqlDataType.Decimal;
            else if (value is DateTime) incoming = SqlDataType.DateTime;
            else incoming = SqlDataType.String;

            if (current == SqlDataType.Null) return incoming;
            if (current == incoming) return current;
            if ((current == SqlDataType.Int64 && incoming == SqlDataType.Double) || (current == SqlDataType.Double && incoming == SqlDataType.Int64)) return SqlDataType.Double;
            return SqlDataType.String;
        }

        private sealed class GroupBucket
        {
            public List<object[]> Rows { get; set; }
        }

        private sealed class EvalContext
        {
            public RowSet RowSet { get; set; }
            public object[] Row { get; set; }
            public List<object[]> GroupRows { get; set; }
            public List<object[]> SourceRows { get; set; }
            public int RowIndex { get; set; }
            public Dictionary<int, object[]> WindowValues { get; set; }
            public ExecutionScope Scope { get; set; }
        }

        private sealed class ExecutionScope
        {
            public ExecutionScope(QueryExecutionOptions options)
            {
                Options = options;
                Tables = options.CaseSensitiveIdentifiers
                    ? new Dictionary<string, RowSet>(StringComparer.Ordinal)
                    : new Dictionary<string, RowSet>(StringComparer.OrdinalIgnoreCase);
            }

            public QueryExecutionOptions Options { get; private set; }
            public Dictionary<string, RowSet> Tables { get; private set; }

            public ExecutionScope Clone()
            {
                ExecutionScope clone = new ExecutionScope(Options);
                foreach (KeyValuePair<string, RowSet> kvp in Tables) clone.Tables[kvp.Key] = kvp.Value;
                return clone;
            }
        }

        private sealed class RowIndexComparer : IComparer<int>
        {
            private readonly QueryExecutor _executor;
            private readonly RowSet _rowSet;
            private readonly List<OrderItem> _orderBy;
            private readonly ExecutionScope _scope;
            private readonly bool _nullsSortFirst;

            public RowIndexComparer(QueryExecutor executor, RowSet rowSet, List<OrderItem> orderBy, ExecutionScope scope, bool nullsSortFirst)
            {
                _executor = executor;
                _rowSet = rowSet;
                _orderBy = orderBy;
                _scope = scope;
                _nullsSortFirst = nullsSortFirst;
            }

            public int Compare(int x, int y)
            {
                for (int i = 0; i < _orderBy.Count; i++)
                {
                    object lx = _executor.EvaluateExpression(_orderBy[i].Expression, new EvalContext { RowSet = _rowSet, Row = _rowSet.Rows[x], Scope = _scope, SourceRows = _rowSet.Rows, RowIndex = x });
                    object ly = _executor.EvaluateExpression(_orderBy[i].Expression, new EvalContext { RowSet = _rowSet, Row = _rowSet.Rows[y], Scope = _scope, SourceRows = _rowSet.Rows, RowIndex = y });
                    int cmp = CompareSqlValues(lx, ly, _nullsSortFirst);
                    if (cmp != 0) return _orderBy[i].Desc ? -cmp : cmp;
                }
                return x.CompareTo(y);
            }
        }

        private sealed class WindowRowComparer : IComparer<int>
        {
            private readonly QueryExecutor _executor;
            private readonly RowSet _rowSet;
            private readonly List<OrderItem> _orderBy;
            private readonly ExecutionScope _scope;
            private readonly bool _nullsSortFirst;

            public WindowRowComparer(QueryExecutor executor, RowSet rowSet, List<OrderItem> orderBy, ExecutionScope scope, bool nullsSortFirst)
            {
                _executor = executor;
                _rowSet = rowSet;
                _orderBy = orderBy;
                _scope = scope;
                _nullsSortFirst = nullsSortFirst;
            }

            public int Compare(int x, int y)
            {
                for (int i = 0; i < _orderBy.Count; i++)
                {
                    object lx = _executor.EvaluateExpression(_orderBy[i].Expression, new EvalContext { RowSet = _rowSet, Row = _rowSet.Rows[x], Scope = _scope, SourceRows = _rowSet.Rows, RowIndex = x });
                    object ly = _executor.EvaluateExpression(_orderBy[i].Expression, new EvalContext { RowSet = _rowSet, Row = _rowSet.Rows[y], Scope = _scope, SourceRows = _rowSet.Rows, RowIndex = y });
                    int cmp = CompareSqlValues(lx, ly, _nullsSortFirst);
                    if (cmp != 0) return _orderBy[i].Desc ? -cmp : cmp;
                }
                return x.CompareTo(y);
            }
        }
    }
}
