using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    private const int DefaultMaxDatabaseBytes = 32 * 1024 * 1024;
    private const int AbsoluteMaxDatabaseBytes = 64 * 1024 * 1024;
    private const int DefaultMaxDatabaseRows = 500000;
    private const int AbsoluteMaxDatabaseRows = 2000000;
    private const int DefaultMaxDatabasePages = 131072;
    private const int AbsoluteMaxDatabasePages = 262144;
    private const int DefaultMaxSqlStatements = 100;
    private const int AbsoluteMaxSqlStatements = 1000;
    private static readonly Regex OperationIdPattern = new Regex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
    private DateTime _deadlineUtc;

    public override async Task<HttpResponseMessage> ExecuteAsync()
    {
        _deadlineUtc = DateTime.UtcNow.AddSeconds(110);
        string operationId = DecodeOperationId(this.Context.OperationId);
        switch (operationId)
        {
            case "QuerySqlite":
                return await ExecuteWithParsedBodyAsync(ExecuteSqliteQuery).ConfigureAwait(false);
            case "ExecuteSqlite":
                return await ExecuteWithParsedBodyAsync(ExecuteSqliteStatements).ConfigureAwait(false);
            case "BulkUpsertSqlite":
                return await ExecuteWithParsedBodyAsync(ExecuteBulkUpsert).ConfigureAwait(false);
            case "ApplyScd2Sqlite":
                return await ExecuteWithParsedBodyAsync(ExecuteScd2).ConfigureAwait(false);
            case "InspectSqlite":
                return await ExecuteWithParsedBodyAsync(ExecuteInspect).ConfigureAwait(false);
            default:
                return CreateErrorResponse(HttpStatusCode.BadRequest, "UNKNOWN_OPERATION", "Unknown operation: " + operationId, null);
        }
    }

    private async Task<HttpResponseMessage> ExecuteWithParsedBodyAsync(Func<JObject, DateTime, HttpResponseMessage> handler)
    {
        DateTime started = DateTime.UtcNow;
        try
        {
            string content = await this.Context.Request.Content.ReadAsStringAsync().ConfigureAwait(false);
            JObject body = ParseBody(content);
            return handler(body, started);
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
        catch (SqliteConnectorException ex)
        {
            return CreateErrorResponse(ex.StatusCode, ex.Code, ex.Message, ex.Details);
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

    private HttpResponseMessage ExecuteSqliteQuery(JObject body, DateTime started)
    {
        SqliteLimits limits = ParseSqliteLimits(body["limits"] as JObject);
        SqliteDatabase database = OpenDatabase(body, limits);
        string sql = BindParameters(GetRequiredString(body, "sql", "The 'sql' field is required."), body["parameters"] as JObject);
        QueryExecutionOptions options = ParseExecutionOptions(body["engineOptions"] as JObject);
        Dictionary<string, RowSet> sourceTables = database.ToRowSets(options);
        SqlParser parser = new SqlParser(sql);
        QueryNode query = parser.ParseQuery();
        RowSet result = new QueryExecutor(options).ExecuteQuery(query, sourceTables);
        if (result.Rows.Count > options.MaxOutputRows)
        {
            throw new SqlExecutionException("RESULT_LIMIT_EXCEEDED", "Query produced " + result.Rows.Count + " rows which exceeds maxOutputRows.", new JObject { ["rowCount"] = result.Rows.Count, ["maxOutputRows"] = options.MaxOutputRows });
        }

        OutputMode mode;
        if (!TryParseOutputMode(body["outputMode"]?.ToString(), out mode))
        {
            throw new DatasetException("INVALID_REQUEST", "The 'outputMode' field must be Csv, RowsAndSchema, or ObjectArray.", null);
        }

        JObject payload = new JObject
        {
            ["mode"] = mode.ToString(),
            ["rowCount"] = result.Rows.Count,
            ["durationMs"] = (long)(DateTime.UtcNow - started).TotalMilliseconds,
            ["database"] = database.RenderMetadata(),
            ["result"] = FormatResult(mode, result)
        };
        return CreateJsonResponse(HttpStatusCode.OK, payload);
    }

    private HttpResponseMessage ExecuteSqliteStatements(JObject body, DateTime started)
    {
        SqliteLimits limits = ParseSqliteLimits(body["limits"] as JObject);
        SqliteDatabase database = OpenDatabase(body, limits);
        string sql = BindParameters(GetRequiredString(body, "sql", "The 'sql' field is required."), body["parameters"] as JObject);
        QueryExecutionOptions options = ParseExecutionOptions(body["engineOptions"] as JObject);
        SqliteChangeSummary changes = new SqliteMutationEngine(this, database, options, CheckBudget).ExecuteStatements(sql);
        return BuildDatabaseWriteResponse(database, changes, started);
    }

    private HttpResponseMessage ExecuteBulkUpsert(JObject body, DateTime started)
    {
        SqliteLimits limits = ParseSqliteLimits(body["limits"] as JObject);
        SqliteDatabase database = OpenDatabase(body, limits);
        QueryExecutionOptions options = ParseExecutionOptions(body["engineOptions"] as JObject);
        SqliteChangeSummary changes = new SqliteMutationEngine(this, database, options, CheckBudget).BulkUpsert(body);
        return BuildDatabaseWriteResponse(database, changes, started);
    }

    private HttpResponseMessage ExecuteScd2(JObject body, DateTime started)
    {
        SqliteLimits limits = ParseSqliteLimits(body["limits"] as JObject);
        SqliteDatabase database = OpenDatabase(body, limits);
        QueryExecutionOptions options = ParseExecutionOptions(body["engineOptions"] as JObject);
        SqliteChangeSummary changes = new SqliteMutationEngine(this, database, options, CheckBudget).ApplyScd2(body);
        return BuildDatabaseWriteResponse(database, changes, started);
    }

    private HttpResponseMessage ExecuteInspect(JObject body, DateTime started)
    {
        SqliteLimits limits = ParseSqliteLimits(body["limits"] as JObject);
        SqliteDatabase database = OpenDatabase(body, limits);
        JObject payload = new JObject
        {
            ["durationMs"] = (long)(DateTime.UtcNow - started).TotalMilliseconds,
            ["database"] = database.RenderMetadata(),
            ["schema"] = database.RenderSchema(),
            ["integrity"] = SqliteIntegrityValidator.Validate(database, CheckBudget)
        };
        return CreateJsonResponse(HttpStatusCode.OK, payload);
    }

    private HttpResponseMessage BuildDatabaseWriteResponse(SqliteDatabase database, SqliteChangeSummary changes, DateTime started)
    {
        CheckBudget();
        byte[] output = new SqliteWriter(database, CheckBudget).Write();
        SqliteDatabase reopened = SqliteDatabase.Open(output, database.Limits, CheckBudget);
        JObject integrity = SqliteIntegrityValidator.ValidateRoundTrip(database, reopened, CheckBudget);
        if (!(integrity["ok"]?.ToObject<bool>() ?? false))
        {
            throw new SqliteConnectorException(HttpStatusCode.InternalServerError, "OUTPUT_INTEGRITY_FAILED", "The rebuilt database did not pass the connector's structural and round-trip validation.", integrity);
        }

        JObject payload = new JObject
        {
            ["databaseBase64"] = Convert.ToBase64String(output),
            ["databaseSizeBytes"] = output.Length,
            ["durationMs"] = (long)(DateTime.UtcNow - started).TotalMilliseconds,
            ["changes"] = changes.ToJson(),
            ["database"] = reopened.RenderMetadata(),
            ["integrity"] = integrity
        };
        return CreateJsonResponse(HttpStatusCode.OK, payload);
    }

    private SqliteDatabase OpenDatabase(JObject body, SqliteLimits limits)
    {
        string encoded = GetRequiredString(body, "databaseBase64", "The 'databaseBase64' field is required.");
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            throw new SqliteConnectorException(HttpStatusCode.BadRequest, "INVALID_BASE64", "The databaseBase64 field is not valid base64.", null);
        }

        if (bytes.Length > limits.MaxDatabaseBytes)
        {
            throw new SqliteConnectorException(HttpStatusCode.RequestEntityTooLarge, "DATABASE_LIMIT_EXCEEDED", "The database exceeds maxDatabaseBytes.", new JObject { ["sizeBytes"] = bytes.Length, ["maxDatabaseBytes"] = limits.MaxDatabaseBytes });
        }
        return SqliteDatabase.Open(bytes, limits, CheckBudget);
    }

    private static SqliteLimits ParseSqliteLimits(JObject body)
    {
        SqliteLimits limits = new SqliteLimits
        {
            MaxDatabaseBytes = body?["maxDatabaseBytes"]?.ToObject<int?>() ?? DefaultMaxDatabaseBytes,
            MaxRows = body?["maxRows"]?.ToObject<int?>() ?? DefaultMaxDatabaseRows,
            MaxPages = body?["maxPages"]?.ToObject<int?>() ?? DefaultMaxDatabasePages,
            MaxSqlStatements = body?["maxSqlStatements"]?.ToObject<int?>() ?? DefaultMaxSqlStatements,
            AllowWalSnapshot = body?["allowWalSnapshot"]?.ToObject<bool?>() ?? false
        };
        limits.Validate();
        return limits;
    }

    private void CheckBudget()
    {
        if (this.CancellationToken.IsCancellationRequested)
        {
            throw new SqliteConnectorException(HttpStatusCode.RequestTimeout, "CANCELLED", "Execution was cancelled.", null);
        }
        if (DateTime.UtcNow >= _deadlineUtc)
        {
            throw new SqliteConnectorException(HttpStatusCode.RequestTimeout, "TIME_BUDGET_EXCEEDED", "Execution exceeded the connector's 110-second safety budget.", null);
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


    private static JObject BuildSelectDatasetRowsRequest(JObject body)
    {
        JObject dataset = GetRequiredObject(body, "dataset", "The 'dataset' field is required.");
        string columns = GetRequiredString(body, "columns", "The 'columns' field is required.");
        return CreateGeneratedQueryRequest(body, "SELECT " + columns + " FROM " + GetDatasetName(dataset), new[] { dataset });
    }

    private static JObject BuildFilterDatasetRowsRequest(JObject body)
    {
        JObject dataset = GetRequiredObject(body, "dataset", "The 'dataset' field is required.");
        string whereClause = GetRequiredString(body, "where", "The 'where' field is required.");
        string selectColumns = GetOptionalString(body, "selectColumns", "*");
        return CreateGeneratedQueryRequest(body, "SELECT " + selectColumns + " FROM " + GetDatasetName(dataset) + " WHERE " + whereClause, new[] { dataset });
    }

    private static JObject BuildJoinDatasetsRequest(JObject body)
    {
        JObject leftDataset = GetRequiredObject(body, "leftDataset", "The 'leftDataset' field is required.");
        JObject rightDataset = GetRequiredObject(body, "rightDataset", "The 'rightDataset' field is required.");
        string leftName = GetDatasetName(leftDataset);
        string rightName = GetDatasetName(rightDataset);
        string joinType = GetRequiredString(body, "joinType", "The 'joinType' field is required.");
        string joinClause = MapJoinType(joinType);
        string onClause = GetRequiredString(body, "on", "The 'on' field is required.");
        string selectColumns = body["selectColumns"]?.ToString();

        if (string.IsNullOrWhiteSpace(selectColumns))
        {
            selectColumns = joinClause == "LEFT ANTI JOIN"
                ? leftName + ".*"
                : joinClause == "RIGHT ANTI JOIN"
                    ? rightName + ".*"
                    : leftName + ".*, " + rightName + ".*";
        }

        string sql = "SELECT " + selectColumns + " FROM " + leftName + " " + joinClause + " " + rightName + " ON " + onClause;
        return CreateGeneratedQueryRequest(body, sql, new[] { leftDataset, rightDataset });
    }

    private static JObject BuildGroupDatasetRowsRequest(JObject body)
    {
        JObject dataset = GetRequiredObject(body, "dataset", "The 'dataset' field is required.");
        JArray aggregations = GetRequiredArray(body, "aggregations", "The 'aggregations' array must contain at least one aggregation.");
        string tableName = GetDatasetName(dataset);
        string groupByColumns = body["groupByColumns"]?.ToString();
        string having = body["having"]?.ToString();
        string orderBy = body["orderBy"]?.ToString();
        JArray rankings = body["rankings"] as JArray;

        List<string> selectParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(groupByColumns))
        {
            selectParts.Add(groupByColumns);
        }

        for (int i = 0; i < aggregations.Count; i++)
        {
            JObject aggregation = aggregations[i] as JObject;
            if (aggregation == null)
            {
                throw new DatasetException("INVALID_REQUEST", "Each 'aggregations' item must be an object.", new JObject { ["index"] = i });
            }
            selectParts.Add(BuildAggregationExpression(aggregation));
        }

        if (selectParts.Count == 0)
        {
            throw new DatasetException("INVALID_REQUEST", "The group by action requires at least one summary or aggregate expression.", null);
        }

        string sql = "SELECT " + string.Join(", ", selectParts) + " FROM " + tableName;
        if (!string.IsNullOrWhiteSpace(groupByColumns))
        {
            sql += " GROUP BY " + groupByColumns;
        }
        if (!string.IsNullOrWhiteSpace(having))
        {
            sql += " HAVING " + having;
        }

        if (rankings != null && rankings.Count > 0)
        {
            List<string> rankingSelects = new List<string> { "grouped.*" };
            for (int i = 0; i < rankings.Count; i++)
            {
                JObject ranking = rankings[i] as JObject;
                if (ranking == null)
                {
                    throw new DatasetException("INVALID_REQUEST", "Each 'rankings' item must be an object.", new JObject { ["index"] = i });
                }
                rankingSelects.Add(BuildRankingExpression(ranking));
            }
            sql = "SELECT " + string.Join(", ", rankingSelects) + " FROM (" + sql + ") grouped";
        }

        if (!string.IsNullOrWhiteSpace(orderBy))
        {
            sql += " ORDER BY " + orderBy;
        }

        return CreateGeneratedQueryRequest(body, sql, new[] { dataset });
    }

    private static JObject BuildDistinctDatasetRowsRequest(JObject body)
    {
        JObject dataset = GetRequiredObject(body, "dataset", "The 'dataset' field is required.");
        string columns = GetOptionalString(body, "columns", "*");
        return CreateGeneratedQueryRequest(body, "SELECT DISTINCT " + columns + " FROM " + GetDatasetName(dataset), new[] { dataset });
    }

    private static JObject BuildSortDatasetRowsRequest(JObject body)
    {
        JObject dataset = GetRequiredObject(body, "dataset", "The 'dataset' field is required.");
        string orderBy = GetRequiredString(body, "orderBy", "The 'orderBy' field is required.");
        string selectColumns = GetOptionalString(body, "selectColumns", "*");
        return CreateGeneratedQueryRequest(body, "SELECT " + selectColumns + " FROM " + GetDatasetName(dataset) + " ORDER BY " + orderBy, new[] { dataset });
    }

    private static JObject BuildUnionDatasetsRequest(JObject body, bool all)
    {
        JArray datasets = GetRequiredArray(body, "datasets", "The 'datasets' array must contain at least two datasets.");
        if (datasets.Count < 2)
        {
            throw new DatasetException("INVALID_REQUEST", "The 'datasets' array must contain at least two datasets.", null);
        }

        string columns = GetOptionalString(body, "columns", "*");
        List<string> selects = new List<string>();
        for (int i = 0; i < datasets.Count; i++)
        {
            JObject dataset = datasets[i] as JObject;
            if (dataset == null)
            {
                throw new DatasetException("INVALID_REQUEST", "Each 'datasets' item must be an object.", new JObject { ["index"] = i });
            }
            selects.Add("SELECT " + columns + " FROM " + GetDatasetName(dataset));
        }

        string separator = all ? " UNION ALL " : " UNION ";
        return CreateGeneratedQueryRequest(body, string.Join(separator, selects), datasets.Cast<JObject>());
    }

    private static JObject CreateGeneratedQueryRequest(JObject body, string sql, IEnumerable<JObject> datasets)
    {
        JObject request = new JObject();
        request["sql"] = sql;
        request["datasets"] = new JArray(datasets.Select(dataset => dataset.DeepClone()));

        JToken outputMode = body["outputMode"];
        if (outputMode != null)
        {
            request["outputMode"] = outputMode.DeepClone();
        }

        JToken engineOptions = body["engineOptions"];
        if (engineOptions != null)
        {
            request["engineOptions"] = engineOptions.DeepClone();
        }

        return request;
    }

    private static JObject GetRequiredObject(JObject body, string propertyName, string message)
    {
        JObject value = body[propertyName] as JObject;
        if (value == null)
        {
            throw new DatasetException("INVALID_REQUEST", message, null);
        }
        return value;
    }

    private static JArray GetRequiredArray(JObject body, string propertyName, string message)
    {
        JArray value = body[propertyName] as JArray;
        if (value == null || value.Count == 0)
        {
            throw new DatasetException("INVALID_REQUEST", message, null);
        }
        return value;
    }

    private static string GetRequiredString(JObject body, string propertyName, string message)
    {
        string value = body[propertyName]?.ToString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DatasetException("INVALID_REQUEST", message, null);
        }
        return value;
    }

    private static string GetOptionalString(JObject body, string propertyName, string defaultValue)
    {
        string value = body[propertyName]?.ToString();
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
    }

    private static string GetDatasetName(JObject dataset)
    {
        string name = dataset["name"]?.ToString();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DatasetException("INVALID_REQUEST", "Each dataset must include a 'name'.", null);
        }
        return name;
    }

    private static string BuildAggregationExpression(JObject aggregation)
    {
        string function = GetRequiredString(aggregation, "function", "Each aggregation requires a 'function'.").ToUpperInvariant();
        string column = aggregation["column"]?.ToString();
        string alias = aggregation["alias"]?.ToString();
        bool distinct = aggregation["distinct"]?.ToObject<bool?>() ?? false;
        string expression;

        switch (function)
        {
            case "COUNT":
                expression = string.IsNullOrWhiteSpace(column)
                    ? "COUNT(*)"
                    : "COUNT(" + (distinct ? "DISTINCT " : string.Empty) + column + ")";
                break;
            case "SUM":
            case "AVG":
            case "MIN":
            case "MAX":
                if (string.IsNullOrWhiteSpace(column))
                {
                    throw new DatasetException("INVALID_REQUEST", "Aggregation function '" + function + "' requires a 'column'.", null);
                }
                expression = function + "(" + (distinct ? "DISTINCT " : string.Empty) + column + ")";
                break;
            default:
                throw new DatasetException("INVALID_REQUEST", "Unsupported aggregation function: " + function + ".", null);
        }

        if (!string.IsNullOrWhiteSpace(alias))
        {
            expression += " AS " + alias;
        }

        return expression;
    }

    private static string BuildRankingExpression(JObject ranking)
    {
        string function = GetRequiredString(ranking, "function", "Each ranking requires a 'function'.").ToUpperInvariant();
        string alias = GetRequiredString(ranking, "alias", "Each ranking requires an 'alias'.");
        string orderBy = GetRequiredString(ranking, "orderBy", "Each ranking requires an 'orderBy'.");
        string partitionBy = ranking["partitionByColumns"]?.ToString();

        if (function != "ROW_NUMBER" && function != "RANK" && function != "DENSE_RANK")
        {
            throw new DatasetException("INVALID_REQUEST", "Unsupported ranking function: " + function + ".", null);
        }

        string overClause = string.IsNullOrWhiteSpace(partitionBy)
            ? "ORDER BY " + orderBy
            : "PARTITION BY " + partitionBy + " ORDER BY " + orderBy;

        return function + "() OVER (" + overClause + ") AS " + alias;
    }

    private static string MapJoinType(string joinType)
    {
        switch (joinType.Trim().ToUpperInvariant())
        {
            case "INNER":
                return "INNER JOIN";
            case "LEFT":
            case "LEFTOUTER":
            case "LEFT OUTER":
                return "LEFT OUTER JOIN";
            case "RIGHT":
            case "RIGHTOUTER":
            case "RIGHT OUTER":
                return "RIGHT OUTER JOIN";
            case "LEFTANTI":
            case "LEFT ANTI":
                return "LEFT ANTI JOIN";
            case "RIGHTANTI":
            case "RIGHT ANTI":
                return "RIGHT ANTI JOIN";
            default:
                throw new DatasetException("INVALID_REQUEST", "The 'joinType' field must be one of: Inner, Left, Right, LeftAnti, RightAnti.", null);
        }
    }

    private static bool TryParseOutputMode(string value, out OutputMode mode)
    {
        mode = OutputMode.RowsAndSchema;
        if (string.IsNullOrWhiteSpace(value))
        {
            mode = OutputMode.RowsAndSchema;
            return true;
        }

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
        bool endedAtRecordBoundary = false;
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
                        endedAtRecordBoundary = false;
                        continue;
                    }

                    inQuotes = false;
                    i++;
                    endedAtRecordBoundary = false;
                    continue;
                }

                field.Append(ch);
                i++;
                endedAtRecordBoundary = false;
                continue;
            }

            if (ch == options.Quote)
            {
                inQuotes = true;
                i++;
                endedAtRecordBoundary = false;
                continue;
            }

            if (ch == options.Separator)
            {
                row.Add(field.ToString());
                field.Length = 0;
                i++;
                endedAtRecordBoundary = false;
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
                endedAtRecordBoundary = true;
                continue;
            }

            field.Append(ch);
            i++;
            endedAtRecordBoundary = false;
        }

        if (inQuotes)
        {
            throw new DatasetException("CSV_PARSE_ERROR", "Unterminated quoted field in CSV payload.", null);
        }

        if (!endedAtRecordBoundary)
        {
            row.Add(field.ToString());
            if (row.Count > 1 || row[0].Length > 0 || rows.Count > 0)
            {
                rows.Add(row);
            }
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
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

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
            case "number":
            case "numeric":
                dataType = SqlDataType.Double;
                return true;
            case "boolean":
            case "bool":
                dataType = SqlDataType.Boolean;
                return true;
            case "datetime":
            case "date":
            case "timestamp":
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
            string decoded = Encoding.UTF8.GetString(data);
            return IsValidOperationId(decoded) ? decoded : operationId;
        }
        catch
        {
            return operationId;
        }
    }

    private static bool IsValidOperationId(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && OperationIdPattern.IsMatch(value);
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

    private new StringContent CreateJsonContent(string json)
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
            "JOIN","INNER","LEFT","RIGHT","FULL","OUTER","CROSS","ANTI","ON",
            "AS","DISTINCT","WITH","UNION","ALL","INTERSECT","EXCEPT","CAST",
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

            if (MatchKeyword("LEFT"))
            {
                if (MatchKeyword("ANTI")) joinType = "LEFTANTI";
                else { MatchKeyword("OUTER"); joinType = "LEFT"; }
            }
            else if (MatchKeyword("RIGHT"))
            {
                if (MatchKeyword("ANTI")) joinType = "RIGHTANTI";
                else { MatchKeyword("OUTER"); joinType = "RIGHT"; }
            }
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
                if (first.Equals("CAST", StringComparison.OrdinalIgnoreCase) && Match(TokenKind.LParen))
                {
                    ExprNode castInput = ParseExpression();
                    ExpectKeyword("AS");
                    Token typeToken = Peek();
                    if (typeToken.Kind != TokenKind.Identifier)
                    {
                        throw Error("Expected data type in CAST expression.");
                    }
                    string targetType = Next().Text;
                    Expect(TokenKind.RParen, "Expected ')' after CAST expression.");
                    FunctionExprNode castFunction = new FunctionExprNode();
                    castFunction.Name = "CAST";
                    castFunction.Arguments = new List<ExprNode>
                    {
                        castInput,
                        NewExpr(new LiteralExprNode { Value = targetType })
                    };
                    return NewExpr(castFunction);
                }

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
            string joinType = join.JoinType.ToUpperInvariant();
            RowSet joinedShape = new RowSet();
            joinedShape.Name = left.Name;
            joinedShape.Columns = new List<SqlColumn>(left.Columns.Count + right.Columns.Count);
            joinedShape.Columns.AddRange(CloneColumns(left.Columns));
            joinedShape.Columns.AddRange(CloneColumns(right.Columns));
            joinedShape.Rows = new List<object[]>();

            int hashLeftColumn, hashRightColumn;
            if (joinType != "CROSS" && TryFindHashJoinColumns(join.On, left, right, out hashLeftColumn, out hashRightColumn))
                return ApplyHashJoin(left, right, join, scope, joinedShape, joinType, hashLeftColumn, hashRightColumn);

            if (joinType == "LEFTANTI" || joinType == "RIGHTANTI")
            {
                RowSet antiOutput = new RowSet();
                antiOutput.Name = joinType == "LEFTANTI" ? left.Name : right.Name;
                antiOutput.Columns = CloneColumns(joinType == "LEFTANTI" ? left.Columns : right.Columns);
                antiOutput.Rows = new List<object[]>();

                if (joinType == "LEFTANTI")
                {
                    for (int i = 0; i < left.Rows.Count; i++)
                    {
                        object[] leftRow = left.Rows[i];
                        bool matchedAny = false;
                        for (int j = 0; j < right.Rows.Count; j++)
                        {
                            object[] combined = CombineRows(leftRow, right.Rows[j]);
                            if (IsTrue(EvaluateExpression(join.On, new EvalContext
                            {
                                RowSet = joinedShape,
                                Row = combined,
                                Scope = scope,
                                SourceRows = joinedShape.Rows
                            })))
                            {
                                matchedAny = true;
                                break;
                            }
                        }

                        if (!matchedAny)
                        {
                            antiOutput.Rows.Add(CloneRow(leftRow));
                        }
                    }
                }
                else
                {
                    for (int j = 0; j < right.Rows.Count; j++)
                    {
                        object[] rightRow = right.Rows[j];
                        bool matchedAny = false;
                        for (int i = 0; i < left.Rows.Count; i++)
                        {
                            object[] combined = CombineRows(left.Rows[i], rightRow);
                            if (IsTrue(EvaluateExpression(join.On, new EvalContext
                            {
                                RowSet = joinedShape,
                                Row = combined,
                                Scope = scope,
                                SourceRows = joinedShape.Rows
                            })))
                            {
                                matchedAny = true;
                                break;
                            }
                        }

                        if (!matchedAny)
                        {
                            antiOutput.Rows.Add(CloneRow(rightRow));
                        }
                    }
                }

                return antiOutput;
            }

            RowSet output = joinedShape;
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

        private RowSet ApplyHashJoin(RowSet left, RowSet right, JoinNode join, ExecutionScope scope, RowSet joinedShape, string joinType, int leftColumn, int rightColumn)
        {
            Dictionary<string, List<int>> lookup = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int j = 0; j < right.Rows.Count; j++)
            {
                string key = BuildHashJoinKey(right.Rows[j][rightColumn]);
                if (key == null) continue;
                List<int> matches;
                if (!lookup.TryGetValue(key, out matches)) lookup[key] = matches = new List<int>();
                matches.Add(j);
            }

            bool[] rightMatched = new bool[right.Rows.Count];
            RowSet output = joinedShape;
            bool anti = joinType == "LEFTANTI" || joinType == "RIGHTANTI";
            if (anti)
            {
                output = new RowSet
                {
                    Name = joinType == "LEFTANTI" ? left.Name : right.Name,
                    Columns = CloneColumns(joinType == "LEFTANTI" ? left.Columns : right.Columns),
                    Rows = new List<object[]>()
                };
            }

            for (int i = 0; i < left.Rows.Count; i++)
            {
                object[] leftRow = left.Rows[i];
                string key = BuildHashJoinKey(leftRow[leftColumn]);
                List<int> candidates;
                bool matchedAny = false;
                if (key != null && lookup.TryGetValue(key, out candidates))
                {
                    for (int c = 0; c < candidates.Count; c++)
                    {
                        int j = candidates[c];
                        object[] combined = CombineRows(leftRow, right.Rows[j]);
                        if (!IsTrue(EvaluateExpression(join.On, new EvalContext
                        {
                            RowSet = joinedShape,
                            Row = combined,
                            Scope = scope,
                            SourceRows = joinedShape.Rows
                        }))) continue;

                        matchedAny = true;
                        rightMatched[j] = true;
                        if (!anti) output.Rows.Add(combined);
                    }
                }

                if (joinType == "LEFTANTI" && !matchedAny) output.Rows.Add(CloneRow(leftRow));
                else if (!anti && !matchedAny && (joinType == "LEFT" || joinType == "FULL"))
                    output.Rows.Add(CombineRows(leftRow, new object[right.Columns.Count]));
            }

            if (joinType == "RIGHTANTI")
            {
                for (int j = 0; j < right.Rows.Count; j++) if (!rightMatched[j]) output.Rows.Add(CloneRow(right.Rows[j]));
            }
            else if (joinType == "RIGHT" || joinType == "FULL")
            {
                for (int j = 0; j < right.Rows.Count; j++)
                    if (!rightMatched[j]) output.Rows.Add(CombineRows(new object[left.Columns.Count], right.Rows[j]));
            }
            return output;
        }

        private bool TryFindHashJoinColumns(ExprNode expression, RowSet left, RowSet right, out int leftColumn, out int rightColumn)
        {
            leftColumn = -1;
            rightColumn = -1;
            BinaryExprNode binary = expression as BinaryExprNode;
            if (binary == null) return false;
            if (string.Equals(binary.Operator, "AND", StringComparison.OrdinalIgnoreCase))
            {
                if (TryFindHashJoinColumns(binary.Left, left, right, out leftColumn, out rightColumn)) return true;
                return TryFindHashJoinColumns(binary.Right, left, right, out leftColumn, out rightColumn);
            }
            if (binary.Operator != "=") return false;
            IdentifierExprNode first = binary.Left as IdentifierExprNode;
            IdentifierExprNode second = binary.Right as IdentifierExprNode;
            if (first == null || second == null) return false;

            int firstLeft, firstRight, secondLeft, secondRight;
            bool firstIsLeft = TryResolveJoinColumn(left, first, out firstLeft);
            bool firstIsRight = TryResolveJoinColumn(right, first, out firstRight);
            bool secondIsLeft = TryResolveJoinColumn(left, second, out secondLeft);
            bool secondIsRight = TryResolveJoinColumn(right, second, out secondRight);
            if (firstIsLeft && secondIsRight && !(firstIsRight && secondIsLeft))
            {
                leftColumn = firstLeft;
                rightColumn = secondRight;
                return true;
            }
            if (secondIsLeft && firstIsRight && !(secondIsRight && firstIsLeft))
            {
                leftColumn = secondLeft;
                rightColumn = firstRight;
                return true;
            }
            return false;
        }

        private bool TryResolveJoinColumn(RowSet rowSet, IdentifierExprNode identifier, out int index)
        {
            index = -1;
            for (int i = 0; i < rowSet.Columns.Count; i++)
            {
                SqlColumn column = rowSet.Columns[i];
                if (!string.IsNullOrWhiteSpace(identifier.Qualifier) && !_nameComparer.Equals(column.TableAlias, identifier.Qualifier)) continue;
                if (!_nameComparer.Equals(column.Name, identifier.Name)) continue;
                if (index >= 0) { index = -1; return false; }
                index = i;
            }
            return index >= 0;
        }

        private static string BuildHashJoinKey(object value)
        {
            if (value == null) return null;
            if (IsIntegralValue(value)) return "N:" + Convert.ToInt64(value);
            if (value is float || value is double || value is decimal)
            {
                double numeric = Convert.ToDouble(value);
                if (!double.IsNaN(numeric) && !double.IsInfinity(numeric) && numeric >= long.MinValue && numeric <= long.MaxValue && Math.Truncate(numeric) == numeric)
                    return "N:" + Convert.ToInt64(numeric);
                return "R:" + numeric.ToString("R");
            }
            string text = value.ToString();
            DateTime date;
            if (DateTime.TryParse(text, out date)) return "D:" + date.Ticks;
            double number;
            if (double.TryParse(text, out number))
            {
                if (number == 0d) number = 0d;
                return "N:" + number.ToString("R");
            }
            bool boolean;
            if (bool.TryParse(text, out boolean)) return boolean ? "B:1" : "B:0";
            return "S:" + text;
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
                IdentifierExprNode identifier = items[i].Expression as IdentifierExprNode;
                string tableAlias = identifier != null && !string.IsNullOrWhiteSpace(identifier.Qualifier)
                    ? identifier.Qualifier
                    : "__result";
                columns.Add(new SqlColumn { Name = alias, TableAlias = tableAlias, DataType = SqlDataType.String, Nullable = true });
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
                case "DATETIME":
                case "TIMESTAMP": return ParseDateLike(args.Count > 0 ? args[0] : null, true);
                case "CAST": return EvalCast(args);
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

        private static object EvalCast(List<object> args)
        {
            if (args.Count < 2 || args[1] == null) return null;
            SqlDataType targetType;
            if (!TryParseDataType(args[1].ToString(), out targetType)) return null;
            return ConvertToType(args[0], targetType);
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

            if (IsIntegralValue(left) && IsIntegralValue(right))
                return Convert.ToInt64(left).CompareTo(Convert.ToInt64(right));
            if (IsNumericValue(left) && IsNumericValue(right))
                return Convert.ToDouble(left).CompareTo(Convert.ToDouble(right));

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

        private static bool IsIntegralValue(object value)
        {
            return value is byte || value is sbyte || value is short || value is ushort
                || value is int || value is uint || value is long || value is bool;
        }

        private static bool IsNumericValue(object value)
        {
            return IsIntegralValue(value) || value is float || value is double || value is decimal;
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
            string type = value is DateTime ? "DateTime" : value.GetType().Name;
            string payload = value is DateTime ? ((DateTime)value).ToString("o") : value.ToString();
            return type + ":" + payload.Length + ":" + payload;
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

    // ==================== SQLite file model and reader ====================

    private sealed class SqliteConnectorException : Exception
    {
        public SqliteConnectorException(HttpStatusCode statusCode, string code, string message, JObject details) : base(message)
        {
            StatusCode = statusCode;
            Code = code;
            Details = details;
        }

        public HttpStatusCode StatusCode { get; private set; }
        public string Code { get; private set; }
        public JObject Details { get; private set; }
    }

    private sealed class SqliteLimits
    {
        public int MaxDatabaseBytes { get; set; }
        public int MaxRows { get; set; }
        public int MaxPages { get; set; }
        public int MaxSqlStatements { get; set; }
        public bool AllowWalSnapshot { get; set; }

        public void Validate()
        {
            if (MaxDatabaseBytes < 512 || MaxDatabaseBytes > AbsoluteMaxDatabaseBytes)
                throw new SqliteConnectorException(HttpStatusCode.BadRequest, "INVALID_LIMIT", "maxDatabaseBytes must be between 512 and " + AbsoluteMaxDatabaseBytes + ".", null);
            if (MaxRows < 1 || MaxRows > AbsoluteMaxDatabaseRows)
                throw new SqliteConnectorException(HttpStatusCode.BadRequest, "INVALID_LIMIT", "maxRows must be between 1 and " + AbsoluteMaxDatabaseRows + ".", null);
            if (MaxPages < 1 || MaxPages > AbsoluteMaxDatabasePages)
                throw new SqliteConnectorException(HttpStatusCode.BadRequest, "INVALID_LIMIT", "maxPages must be between 1 and " + AbsoluteMaxDatabasePages + ".", null);
            if (MaxSqlStatements < 1 || MaxSqlStatements > AbsoluteMaxSqlStatements)
                throw new SqliteConnectorException(HttpStatusCode.BadRequest, "INVALID_LIMIT", "maxSqlStatements must be between 1 and " + AbsoluteMaxSqlStatements + ".", null);
        }
    }

    private sealed class SqliteSchemaEntry
    {
        public long RowId { get; set; }
        public string Type { get; set; }
        public string Name { get; set; }
        public string TableName { get; set; }
        public int RootPage { get; set; }
        public int OutputRootPage { get; set; }
        public string Sql { get; set; }
    }

    private sealed class SqliteColumnDef
    {
        public string Name { get; set; }
        public string DeclaredType { get; set; }
        public bool NotNull { get; set; }
        public bool PrimaryKey { get; set; }
        public bool Unique { get; set; }
        public bool IntegerPrimaryKey { get; set; }
        public bool AutoIncrement { get; set; }
        public string DefaultSql { get; set; }
        public string Collation { get; set; }
    }

    private sealed class SqliteUniqueKey
    {
        public string Name { get; set; }
        public bool PrimaryKey { get; set; }
        public List<string> Columns { get; set; }
        public List<bool> Descending { get; set; }
        public List<string> Collations { get; set; }
    }

    private sealed class SqliteForeignKeyDef
    {
        public List<string> ChildColumns { get; set; }
        public string ParentTable { get; set; }
        public List<string> ParentColumns { get; set; }
    }

    private sealed class SqliteIndexDef
    {
        public SqliteSchemaEntry Schema { get; set; }
        public bool Unique { get; set; }
        public bool AutoIndex { get; set; }
        public List<string> Columns { get; set; }
        public List<bool> Descending { get; set; }
        public List<string> Collations { get; set; }
        public bool Supported { get; set; }
        public string UnsupportedReason { get; set; }
    }

    private sealed class SqliteRow
    {
        public long RowId { get; set; }
        public object[] Values { get; set; }

        public SqliteRow Clone()
        {
            object[] values = new object[Values.Length];
            for (int i = 0; i < Values.Length; i++)
            {
                byte[] blob = Values[i] as byte[];
                values[i] = blob == null ? Values[i] : (object)blob.ToArray();
            }
            return new SqliteRow { RowId = RowId, Values = values };
        }
    }

    private sealed class SqliteTable
    {
        public SqliteSchemaEntry Schema { get; set; }
        public List<SqliteColumnDef> Columns { get; set; }
        public List<SqliteUniqueKey> UniqueKeys { get; set; }
        public List<SqliteForeignKeyDef> ForeignKeys { get; set; }
        public List<SqliteIndexDef> Indexes { get; set; }
        public List<SqliteRow> Rows { get; set; }
        public int IntegerPrimaryKeyIndex { get; set; }
        public bool Writable { get; set; }
        public string UnsupportedReason { get; set; }

        public string Name { get { return Schema.Name; } }

        public int FindColumn(string name)
        {
            for (int i = 0; i < Columns.Count; i++)
                if (string.Equals(Columns[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        public long NextRowId()
        {
            if (Rows.Count == 0) return 1;
            long max = Rows.Max(r => r.RowId);
            if (max == long.MaxValue) throw new SqliteConnectorException(HttpStatusCode.BadRequest, "ROWID_EXHAUSTED", "The table '" + Name + "' has exhausted positive rowids.", null);
            return Math.Max(1, max + 1);
        }

        public RowSet ToRowSet(bool includeInternalRowId)
        {
            RowSet result = new RowSet { Name = Name, Columns = new List<SqlColumn>(), Rows = new List<object[]>(Rows.Count) };
            if (includeInternalRowId)
                result.Columns.Add(new SqlColumn { Name = "__pa_rowid", TableAlias = Name, DataType = SqlDataType.Int64, Nullable = false });
            for (int i = 0; i < Columns.Count; i++)
                result.Columns.Add(new SqlColumn { Name = Columns[i].Name, TableAlias = Name, DataType = SqliteDatabase.InferSqliteColumnType(Rows, i), Nullable = !Columns[i].NotNull });
            for (int r = 0; r < Rows.Count; r++)
            {
                int offset = includeInternalRowId ? 1 : 0;
                object[] values = new object[Columns.Count + offset];
                if (includeInternalRowId) values[0] = Rows[r].RowId;
                Array.Copy(Rows[r].Values, 0, values, offset, Columns.Count);
                result.Rows.Add(values);
            }
            return result;
        }
    }

    private sealed class SqliteRawCell
    {
        public long RowId { get; set; }
        public byte[] Payload { get; set; }
    }

    private sealed class SqliteDatabase
    {
        private readonly byte[] _bytes;
        private readonly Action _checkBudget;
        private int _totalRows;

        private SqliteDatabase(byte[] bytes, SqliteLimits limits, Action checkBudget)
        {
            _bytes = bytes;
            Limits = limits;
            _checkBudget = checkBudget;
            SchemaEntries = new List<SqliteSchemaEntry>();
            Tables = new Dictionary<string, SqliteTable>(StringComparer.OrdinalIgnoreCase);
            Indexes = new List<SqliteIndexDef>();
        }

        public SqliteLimits Limits { get; private set; }
        public int PageSize { get; private set; }
        public int ReservedBytes { get; private set; }
        public int UsableSize { get; private set; }
        public int PageCount { get; private set; }
        public int EncodingId { get; private set; }
        public Encoding TextEncoding { get; private set; }
        public bool WalMode { get; private set; }
        public bool AutoVacuum { get; private set; }
        public uint ChangeCounter { get; private set; }
        public uint SchemaCookie { get; set; }
        public uint DefaultCacheSize { get; private set; }
        public uint UserVersion { get; set; }
        public uint ApplicationId { get; set; }
        public uint WriteLibraryVersion { get; private set; }
        public List<SqliteSchemaEntry> SchemaEntries { get; private set; }
        public Dictionary<string, SqliteTable> Tables { get; private set; }
        public List<SqliteIndexDef> Indexes { get; private set; }
        public bool SchemaChanged { get; set; }

        public static SqliteDatabase Open(byte[] bytes, SqliteLimits limits, Action checkBudget)
        {
            SqliteDatabase db = new SqliteDatabase(bytes, limits, checkBudget);
            db.Parse();
            return db;
        }

        private void Parse()
        {
            if (_bytes == null || _bytes.Length < 512) Fail("INVALID_SQLITE_FILE", "The input is too small to be a SQLite database.", null);
            byte[] magic = Encoding.ASCII.GetBytes("SQLite format 3\0");
            for (int i = 0; i < magic.Length; i++) if (_bytes[i] != magic[i]) Fail("INVALID_SQLITE_FILE", "The input does not begin with the SQLite format 3 header.", null);

            int ps = ReadUInt16(_bytes, 16);
            PageSize = ps == 1 ? 65536 : ps;
            if (PageSize < 512 || PageSize > 65536 || (PageSize & (PageSize - 1)) != 0)
                Fail("INVALID_SQLITE_HEADER", "The SQLite page size is invalid.", new JObject { ["pageSize"] = PageSize });
            if (_bytes.Length % PageSize != 0)
                Fail("TRUNCATED_SQLITE_FILE", "The database length is not an exact multiple of its page size.", new JObject { ["sizeBytes"] = _bytes.Length, ["pageSize"] = PageSize });

            PageCount = _bytes.Length / PageSize;
            if (PageCount > Limits.MaxPages) Fail("PAGE_LIMIT_EXCEEDED", "The database exceeds maxPages.", new JObject { ["pageCount"] = PageCount, ["maxPages"] = Limits.MaxPages });
            ReservedBytes = _bytes[20];
            UsableSize = PageSize - ReservedBytes;
            if (UsableSize < 480) Fail("INVALID_SQLITE_HEADER", "The usable page size is below SQLite's 480-byte minimum.", null);
            if (_bytes[21] != 64 || _bytes[22] != 32 || _bytes[23] != 32) Fail("INVALID_SQLITE_HEADER", "The SQLite payload fraction bytes are invalid.", null);

            WalMode = _bytes[18] == 2;
            if (_bytes[19] != 1 && _bytes[19] != 2) Fail("UNSUPPORTED_SQLITE_FORMAT", "The database read format version is unsupported.", new JObject { ["readVersion"] = _bytes[19] });
            EncodingId = (int)ReadUInt32(_bytes, 56);
            if (EncodingId == 1) TextEncoding = Encoding.UTF8;
            else if (EncodingId == 2) TextEncoding = Encoding.Unicode;
            else if (EncodingId == 3) TextEncoding = Encoding.BigEndianUnicode;
            else Fail("INVALID_SQLITE_HEADER", "The SQLite text encoding id is invalid.", new JObject { ["encodingId"] = EncodingId });

            ChangeCounter = ReadUInt32(_bytes, 24);
            SchemaCookie = ReadUInt32(_bytes, 40);
            DefaultCacheSize = ReadUInt32(_bytes, 48);
            AutoVacuum = ReadUInt32(_bytes, 52) != 0;
            UserVersion = ReadUInt32(_bytes, 60);
            ApplicationId = ReadUInt32(_bytes, 68);
            WriteLibraryVersion = ReadUInt32(_bytes, 96);

            List<SqliteRawCell> masterCells = CollectTableCells(1);
            for (int i = 0; i < masterCells.Count; i++)
            {
                object[] values = DecodeRecord(masterCells[i].Payload);
                if (values.Length < 5) Fail("INVALID_SQLITE_SCHEMA", "A sqlite_schema record has fewer than five columns.", null);
                SchemaEntries.Add(new SqliteSchemaEntry
                {
                    RowId = masterCells[i].RowId,
                    Type = AsText(values[0]),
                    Name = AsText(values[1]),
                    TableName = AsText(values[2]),
                    RootPage = AsInt32(values[3]),
                    Sql = AsText(values[4])
                });
            }
            for (int i = 0; i < SchemaEntries.Count; i++)
            {
                SqliteSchemaEntry entry = SchemaEntries[i];
                if (string.Equals(entry.Type, "table", StringComparison.OrdinalIgnoreCase) && entry.RootPage > 0) ParseTable(entry);
            }
            ParseIndexes();
        }

        private void ParseTable(SqliteSchemaEntry entry)
        {
            SqliteDdlResult ddl = SqliteDdl.ParseTable(entry.Sql, entry.Name);
            if (!ddl.Writable && ddl.UnsupportedReason != null && ddl.UnsupportedReason.StartsWith("WITHOUT ROWID", StringComparison.Ordinal))
                Fail("WITHOUT_ROWID_UNSUPPORTED", "Table '" + entry.Name + "' uses WITHOUT ROWID index-b-tree storage, which this connector does not materialize.", null);
            if (!ddl.Writable && ddl.UnsupportedReason != null && ddl.UnsupportedReason.StartsWith("Generated columns", StringComparison.Ordinal))
                Fail("GENERATED_COLUMN_UNSUPPORTED", "Table '" + entry.Name + "' contains generated columns, which this connector cannot recompute safely.", null);
            List<SqliteRawCell> cells = CollectTableCells(entry.RootPage);
            int observedColumns = 0;
            List<object[]> decoded = new List<object[]>(cells.Count);
            for (int i = 0; i < cells.Count; i++)
            {
                object[] values = DecodeRecord(cells[i].Payload);
                observedColumns = Math.Max(observedColumns, values.Length);
                decoded.Add(values);
            }
            if (ddl.Columns.Count == 0 && observedColumns > 0)
            {
                for (int i = 0; i < observedColumns; i++) ddl.Columns.Add(new SqliteColumnDef { Name = "column" + (i + 1), DeclaredType = "", Collation = "BINARY" });
                ddl.Writable = false;
                ddl.UnsupportedReason = "The CREATE TABLE statement could not be parsed.";
            }

            SqliteTable table = new SqliteTable
            {
                Schema = entry,
                Columns = ddl.Columns,
                UniqueKeys = ddl.UniqueKeys,
                ForeignKeys = ddl.ForeignKeys,
                Indexes = new List<SqliteIndexDef>(),
                Rows = new List<SqliteRow>(cells.Count),
                IntegerPrimaryKeyIndex = ddl.IntegerPrimaryKeyIndex,
                Writable = ddl.Writable,
                UnsupportedReason = ddl.UnsupportedReason
            };
            for (int i = 0; i < cells.Count; i++)
            {
                object[] values = new object[table.Columns.Count];
                Array.Copy(decoded[i], values, Math.Min(decoded[i].Length, values.Length));
                if (table.IntegerPrimaryKeyIndex >= 0 && table.IntegerPrimaryKeyIndex < values.Length && values[table.IntegerPrimaryKeyIndex] == null)
                    values[table.IntegerPrimaryKeyIndex] = cells[i].RowId;
                table.Rows.Add(new SqliteRow { RowId = cells[i].RowId, Values = values });
            }
            table.Rows.Sort((a, b) => a.RowId.CompareTo(b.RowId));
            _totalRows += table.Rows.Count;
            if (_totalRows > Limits.MaxRows) Fail("ROW_LIMIT_EXCEEDED", "The database exceeds maxRows across materialized tables.", new JObject { ["rowCount"] = _totalRows, ["maxRows"] = Limits.MaxRows });
            Tables[entry.Name] = table;
        }

        private void ParseIndexes()
        {
            foreach (SqliteSchemaEntry entry in SchemaEntries)
            {
                if (!string.Equals(entry.Type, "index", StringComparison.OrdinalIgnoreCase)) continue;
                SqliteIndexDef index = SqliteDdl.ParseIndex(entry);
                SqliteTable table;
                if (!Tables.TryGetValue(entry.TableName, out table))
                {
                    index.Supported = false;
                    index.UnsupportedReason = "The indexed table could not be materialized.";
                }
                else if (index.AutoIndex)
                {
                    SqliteUniqueKey key = table.UniqueKeys.FirstOrDefault(k => string.Equals(k.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
                    if (key == null)
                    {
                        index.Supported = false;
                        index.UnsupportedReason = "The automatic index columns could not be derived from CREATE TABLE.";
                    }
                    else
                    {
                        index.Unique = true;
                        index.Columns = new List<string>(key.Columns);
                        index.Descending = new List<bool>(key.Descending);
                        index.Collations = new List<string>(key.Collations);
                    }
                }
                if (table != null) table.Indexes.Add(index);
                Indexes.Add(index);
            }
        }

        public void EnsureWritable()
        {
            if (WalMode && !Limits.AllowWalSnapshot) Fail("WAL_SNAPSHOT_UNSAFE", "This main database file is marked for WAL mode. Supply a checkpointed database or set limits.allowWalSnapshot=true.", null);
            if (ReservedBytes != 0) Fail("RESERVED_BYTES_UNSUPPORTED", "Writing databases with reserved per-page bytes is not supported.", new JObject { ["reservedBytes"] = ReservedBytes });
            if (AutoVacuum) Fail("AUTO_VACUUM_UNSUPPORTED", "Writing auto-vacuum databases would require pointer-map maintenance and is rejected.", null);
            foreach (SqliteTable table in Tables.Values) if (!table.Writable) Fail("UNSUPPORTED_SCHEMA", "Table '" + table.Name + "' is read-only: " + table.UnsupportedReason, null);
            foreach (SqliteIndexDef index in Indexes) if (!index.Supported) Fail("UNSUPPORTED_INDEX", "Index '" + index.Schema.Name + "' cannot be rebuilt: " + index.UnsupportedReason, null);
            foreach (SqliteSchemaEntry entry in SchemaEntries)
            {
                if (string.Equals(entry.Type, "trigger", StringComparison.OrdinalIgnoreCase))
                    Fail("TRIGGERS_UNSUPPORTED", "Writes are rejected because trigger '" + entry.Name + "' cannot be executed by the in-memory mutation engine.", null);
                if (entry.RootPage == 0 && entry.Sql != null && entry.Sql.TrimStart().StartsWith("CREATE VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase))
                    Fail("VIRTUAL_TABLE_UNSUPPORTED", "Databases containing virtual tables cannot be rewritten safely.", new JObject { ["table"] = entry.Name });
            }
            foreach (SqliteTable table in Tables.Values)
            {
                string sql = table.Schema.Sql ?? "";
                if (Regex.IsMatch(sql, @"\bCHECK\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    Fail("CHECK_CONSTRAINTS_UNSUPPORTED", "Writes are rejected because table '" + table.Name + "' contains CHECK constraints that the mutation engine cannot guarantee.", null);
                if (Regex.IsMatch(sql, @"\)\s*STRICT\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    Fail("STRICT_TABLE_UNSUPPORTED", "Writes are rejected because STRICT table '" + table.Name + "' requires native SQLite type enforcement.", null);
            }
        }

        public Dictionary<string, RowSet> ToRowSets(QueryExecutionOptions options)
        {
            Dictionary<string, RowSet> result = options.CaseSensitiveIdentifiers
                ? new Dictionary<string, RowSet>(StringComparer.Ordinal)
                : new Dictionary<string, RowSet>(StringComparer.OrdinalIgnoreCase);
            foreach (SqliteTable table in Tables.Values) result[table.Name] = table.ToRowSet(false);
            RowSet schema = new RowSet
            {
                Name = "sqlite_master",
                Columns = new List<SqlColumn>
                {
                    new SqlColumn { Name = "type", TableAlias = "sqlite_master", DataType = SqlDataType.String, Nullable = false },
                    new SqlColumn { Name = "name", TableAlias = "sqlite_master", DataType = SqlDataType.String, Nullable = false },
                    new SqlColumn { Name = "tbl_name", TableAlias = "sqlite_master", DataType = SqlDataType.String, Nullable = false },
                    new SqlColumn { Name = "rootpage", TableAlias = "sqlite_master", DataType = SqlDataType.Int64, Nullable = false },
                    new SqlColumn { Name = "sql", TableAlias = "sqlite_master", DataType = SqlDataType.String, Nullable = true }
                },
                Rows = SchemaEntries.Select(e => new object[] { e.Type, e.Name, e.TableName, (long)e.RootPage, e.Sql }).ToList()
            };
            result["sqlite_master"] = schema;
            result["sqlite_schema"] = schema.Clone();
            result["sqlite_schema"].Name = "sqlite_schema";
            return result;
        }

        public JObject RenderMetadata()
        {
            JArray warnings = new JArray();
            if (WalMode) warnings.Add("The input header is in WAL mode; an unprovided -wal file may contain newer committed data.");
            return new JObject
            {
                ["pageSize"] = PageSize, ["pageCount"] = PageCount,
                ["encoding"] = EncodingId == 1 ? "UTF-8" : EncodingId == 2 ? "UTF-16LE" : "UTF-16BE",
                ["walMode"] = WalMode, ["autoVacuum"] = AutoVacuum, ["userVersion"] = UserVersion,
                ["applicationId"] = ApplicationId, ["tableCount"] = Tables.Count, ["indexCount"] = Indexes.Count,
                ["rowCount"] = _totalRows, ["warnings"] = warnings
            };
        }

        public JArray RenderSchema()
        {
            JArray tables = new JArray();
            foreach (SqliteTable table in Tables.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            {
                JArray columns = new JArray();
                for (int i = 0; i < table.Columns.Count; i++)
                {
                    SqliteColumnDef c = table.Columns[i];
                    columns.Add(new JObject { ["name"] = c.Name, ["declaredType"] = c.DeclaredType, ["notNull"] = c.NotNull, ["primaryKey"] = c.PrimaryKey, ["integerPrimaryKey"] = c.IntegerPrimaryKey, ["unique"] = c.Unique });
                }
                tables.Add(new JObject
                {
                    ["name"] = table.Name, ["rootPage"] = table.Schema.RootPage, ["rowCount"] = table.Rows.Count,
                    ["writable"] = table.Writable, ["unsupportedReason"] = table.UnsupportedReason == null ? JValue.CreateNull() : new JValue(table.UnsupportedReason),
                    ["sql"] = table.Schema.Sql, ["columns"] = columns, ["indexes"] = new JArray(table.Indexes.Select(x => x.Schema.Name))
                });
            }
            return tables;
        }

        private List<SqliteRawCell> CollectTableCells(int rootPage)
        {
            List<SqliteRawCell> result = new List<SqliteRawCell>();
            CollectTableCellsRecursive(rootPage, new HashSet<int>(), result);
            return result;
        }

        private void CollectTableCellsRecursive(int pageNumber, HashSet<int> visited, List<SqliteRawCell> result)
        {
            _checkBudget();
            ValidatePageNumber(pageNumber);
            if (!visited.Add(pageNumber)) Fail("BTREE_CYCLE", "A cycle was detected in a table b-tree.", new JObject { ["page"] = pageNumber });
            int pageOffset = (pageNumber - 1) * PageSize;
            int header = pageOffset + (pageNumber == 1 ? 100 : 0);
            EnsureRange(header, 8);
            int type = _bytes[header];
            int cellCount = ReadUInt16(_bytes, header + 3);
            int headerSize = type == 5 ? 12 : 8;
            EnsureRange(header + headerSize, cellCount * 2);
            if (type == 5)
            {
                EnsureRange(header, 12);
                for (int i = 0; i < cellCount; i++)
                {
                    int cellOffset = pageOffset + ReadUInt16(_bytes, header + headerSize + i * 2);
                    EnsurePageRange(pageNumber, cellOffset, 4);
                    CollectTableCellsRecursive((int)ReadUInt32(_bytes, cellOffset), visited, result);
                }
                CollectTableCellsRecursive((int)ReadUInt32(_bytes, header + 8), visited, result);
                return;
            }
            if (type != 13) Fail("UNSUPPORTED_BTREE_PAGE", "Expected a table b-tree page but found page type " + type + ".", new JObject { ["page"] = pageNumber, ["pageType"] = type });
            for (int i = 0; i < cellCount; i++)
            {
                int cellOffset = pageOffset + ReadUInt16(_bytes, header + headerSize + i * 2);
                EnsurePageRange(pageNumber, cellOffset, 1);
                int cursor = cellOffset;
                ulong payloadLengthValue = ReadVarint(_bytes, ref cursor, pageOffset + UsableSize);
                if (payloadLengthValue > int.MaxValue) Fail("PAYLOAD_LIMIT_EXCEEDED", "A SQLite record payload is too large.", null);
                int payloadLength = (int)payloadLengthValue;
                ulong rowIdValue = ReadVarint(_bytes, ref cursor, pageOffset + UsableSize);
                int local = LocalPayloadSize(payloadLength, true);
                EnsurePageRange(pageNumber, cursor, local + (local < payloadLength ? 4 : 0));
                byte[] payload = new byte[payloadLength];
                Buffer.BlockCopy(_bytes, cursor, payload, 0, local);
                if (local < payloadLength) ReadOverflow((int)ReadUInt32(_bytes, cursor + local), payload, local);
                result.Add(new SqliteRawCell { RowId = unchecked((long)rowIdValue), Payload = payload });
            }
        }

        private void ReadOverflow(int pageNumber, byte[] destination, int written)
        {
            HashSet<int> visited = new HashSet<int>();
            while (written < destination.Length)
            {
                _checkBudget();
                ValidatePageNumber(pageNumber);
                if (!visited.Add(pageNumber)) Fail("OVERFLOW_CYCLE", "A cycle was detected in an overflow-page chain.", new JObject { ["page"] = pageNumber });
                int offset = (pageNumber - 1) * PageSize;
                int count = Math.Min(UsableSize - 4, destination.Length - written);
                EnsureRange(offset, 4 + count);
                Buffer.BlockCopy(_bytes, offset + 4, destination, written, count);
                written += count;
                pageNumber = (int)ReadUInt32(_bytes, offset);
                if (written < destination.Length && pageNumber == 0) Fail("TRUNCATED_OVERFLOW_CHAIN", "A record's overflow chain ended before the payload was complete.", null);
            }
        }

        private int LocalPayloadSize(int payloadLength, bool tableLeaf)
        {
            int maxLocal = tableLeaf ? UsableSize - 35 : ((UsableSize - 12) * 64 / 255) - 23;
            if (payloadLength <= maxLocal) return payloadLength;
            int minLocal = ((UsableSize - 12) * 32 / 255) - 23;
            int local = minLocal + (payloadLength - minLocal) % (UsableSize - 4);
            return local <= maxLocal ? local : minLocal;
        }

        private object[] DecodeRecord(byte[] payload)
        {
            int cursor = 0;
            ulong headerLengthValue = ReadVarint(payload, ref cursor, payload.Length);
            if (headerLengthValue > (ulong)payload.Length || headerLengthValue < (ulong)cursor) Fail("INVALID_RECORD", "A SQLite record header length is invalid.", null);
            int headerLength = (int)headerLengthValue;
            List<ulong> types = new List<ulong>();
            while (cursor < headerLength) types.Add(ReadVarint(payload, ref cursor, headerLength));
            int dataOffset = headerLength;
            object[] values = new object[types.Count];
            for (int i = 0; i < types.Count; i++)
            {
                int size = SerialTypeSize(types[i]);
                if (dataOffset + size > payload.Length) Fail("TRUNCATED_RECORD", "A SQLite record value extends beyond its payload.", null);
                values[i] = DecodeValue(payload, dataOffset, types[i], size);
                dataOffset += size;
            }
            return values;
        }

        private object DecodeValue(byte[] payload, int offset, ulong serialType, int size)
        {
            if (serialType == 0) return null;
            if (serialType == 8) return 0L;
            if (serialType == 9) return 1L;
            if (serialType >= 1 && serialType <= 6) return ReadSignedInteger(payload, offset, size);
            if (serialType == 7)
            {
                byte[] little = new byte[8];
                for (int i = 0; i < 8; i++) little[7 - i] = payload[offset + i];
                return BitConverter.ToDouble(little, 0);
            }
            if (serialType == 10 || serialType == 11) Fail("RESERVED_SERIAL_TYPE", "A record uses a reserved serial type.", null);
            byte[] bytes = new byte[size];
            Buffer.BlockCopy(payload, offset, bytes, 0, size);
            return (serialType & 1UL) == 0 ? (object)bytes : TextEncoding.GetString(bytes);
        }

        private static int SerialTypeSize(ulong serialType)
        {
            if (serialType == 0 || serialType == 8 || serialType == 9) return 0;
            if (serialType >= 1 && serialType <= 4) return (int)serialType;
            if (serialType == 5) return 6;
            if (serialType == 6 || serialType == 7) return 8;
            if (serialType >= 12)
            {
                ulong size = (serialType - 12) / 2;
                if (size > int.MaxValue) throw new SqliteConnectorException(HttpStatusCode.BadRequest, "PAYLOAD_LIMIT_EXCEEDED", "A record value is too large.", null);
                return (int)size;
            }
            return 0;
        }

        private static long ReadSignedInteger(byte[] bytes, int offset, int size)
        {
            ulong value = 0;
            for (int i = 0; i < size; i++) value = (value << 8) | (ulong)bytes[offset + i];
            if (size < 8 && (bytes[offset] & 0x80) != 0) value |= ulong.MaxValue << (size * 8);
            return unchecked((long)value);
        }

        private static ulong ReadVarint(byte[] bytes, ref int offset, int limit)
        {
            ulong value = 0;
            for (int i = 0; i < 9; i++)
            {
                if (offset >= limit) throw new SqliteConnectorException(HttpStatusCode.BadRequest, "TRUNCATED_VARINT", "A SQLite varint is truncated.", null);
                byte b = bytes[offset++];
                if (i == 8) return (value << 8) | (ulong)b;
                value = (value << 7) | ((ulong)b & 0x7fUL);
                if ((b & 0x80) == 0) return value;
            }
            return value;
        }

        private static int ReadUInt16(byte[] bytes, int offset) { return (bytes[offset] << 8) | bytes[offset + 1]; }
        private static uint ReadUInt32(byte[] bytes, int offset) { return ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) | ((uint)bytes[offset + 2] << 8) | bytes[offset + 3]; }

        private void ValidatePageNumber(int page)
        {
            if (page < 1 || page > PageCount) Fail("INVALID_PAGE_REFERENCE", "A b-tree references a page outside the database.", new JObject { ["page"] = page, ["pageCount"] = PageCount });
        }

        private void EnsureRange(int offset, int count)
        {
            if (offset < 0 || count < 0 || offset > _bytes.Length - count) Fail("TRUNCATED_SQLITE_FILE", "A SQLite structure extends beyond the input file.", new JObject { ["offset"] = offset, ["count"] = count });
        }

        private void EnsurePageRange(int pageNumber, int offset, int count)
        {
            int start = (pageNumber - 1) * PageSize;
            if (offset < start || count < 0 || offset > start + UsableSize - count) Fail("INVALID_PAGE_STRUCTURE", "A SQLite cell extends outside its usable page area.", new JObject { ["page"] = pageNumber, ["offset"] = offset, ["count"] = count });
        }

        private static string AsText(object value) { return value == null ? null : value.ToString(); }
        private static int AsInt32(object value)
        {
            long result;
            return value != null && long.TryParse(value.ToString(), out result) && result >= 0 && result <= int.MaxValue ? (int)result : 0;
        }

        public static SqlDataType InferSqliteColumnType(List<SqliteRow> rows, int column)
        {
            bool text = false, integer = false, real = false, blob = false;
            for (int i = 0; i < rows.Count; i++)
            {
                object value = rows[i].Values[column];
                if (value == null) continue;
                if (value is byte[]) blob = true;
                else if (value is long || value is int) integer = true;
                else if (value is double || value is float || value is decimal) real = true;
                else text = true;
            }
            if (blob || text) return SqlDataType.String;
            if (real) return SqlDataType.Double;
            if (integer) return SqlDataType.Int64;
            return SqlDataType.Null;
        }

        private static void Fail(string code, string message, JObject details) { throw new SqliteConnectorException(HttpStatusCode.BadRequest, code, message, details); }
    }

    private sealed class SqliteDdlResult
    {
        public List<SqliteColumnDef> Columns { get; set; }
        public List<SqliteUniqueKey> UniqueKeys { get; set; }
        public List<SqliteForeignKeyDef> ForeignKeys { get; set; }
        public int IntegerPrimaryKeyIndex { get; set; }
        public bool Writable { get; set; }
        public string UnsupportedReason { get; set; }
    }


    private static class SqliteDdl
    {
        private static readonly string[] ColumnConstraintWords = { "CONSTRAINT", "PRIMARY", "NOT", "UNIQUE", "CHECK", "DEFAULT", "COLLATE", "REFERENCES", "GENERATED", "AS" };

        public static SqliteDdlResult ParseTable(string sql, string tableName)
        {
            SqliteDdlResult result = new SqliteDdlResult
            {
                Columns = new List<SqliteColumnDef>(),
                UniqueKeys = new List<SqliteUniqueKey>(),
                ForeignKeys = new List<SqliteForeignKeyDef>(),
                IntegerPrimaryKeyIndex = -1,
                Writable = true
            };
            if (string.IsNullOrWhiteSpace(sql))
            {
                result.Writable = false;
                result.UnsupportedReason = "The table has no stored CREATE TABLE SQL.";
                return result;
            }
            string upper = sql.ToUpperInvariant();
            if (upper.Contains("WITHOUT ROWID")) { result.Writable = false; result.UnsupportedReason = "WITHOUT ROWID tables use index b-trees."; }
            if (upper.Contains("CREATE VIRTUAL TABLE")) { result.Writable = false; result.UnsupportedReason = "Virtual tables do not have ordinary table b-tree storage."; }
            if (upper.Contains(" GENERATED ") || upper.Contains(" AS (")) { result.Writable = false; result.UnsupportedReason = "Generated columns are not supported for rewriting."; }

            int open = FindCharOutside(sql, '(', 0);
            int close = open < 0 ? -1 : FindMatchingParen(sql, open);
            if (open < 0 || close < 0) return Unsupported(result, "The CREATE TABLE column list is malformed.");
            List<string> parts = SplitTopLevel(sql.Substring(open + 1, close - open - 1), ',');
            List<SqliteUniqueKey> pendingKeys = new List<SqliteUniqueKey>();
            for (int i = 0; i < parts.Count; i++)
            {
                string part = parts[i].Trim();
                if (part.Length == 0) continue;
                string constraintBody = StripConstraintPrefix(part);
                if (StartsWord(constraintBody, "PRIMARY") || StartsWord(constraintBody, "UNIQUE"))
                {
                    bool primary = StartsWord(constraintBody, "PRIMARY");
                    List<string> cols;
                    List<bool> desc;
                    List<string> collations;
                    if (!TryParseIndexedColumns(constraintBody, out cols, out desc, out collations))
                        return Unsupported(result, "A table-level PRIMARY KEY or UNIQUE constraint is not a simple column list.");
                    pendingKeys.Add(new SqliteUniqueKey { PrimaryKey = primary, Columns = cols, Descending = desc, Collations = collations });
                    continue;
                }
                if (StartsWord(constraintBody, "FOREIGN"))
                {
                    SqliteForeignKeyDef foreignKey;
                    if (!TryParseForeignKey(constraintBody, null, out foreignKey))
                        return Unsupported(result, "A table-level FOREIGN KEY constraint could not be parsed.");
                    result.ForeignKeys.Add(foreignKey);
                    continue;
                }
                if (StartsWord(constraintBody, "CHECK")) continue;

                int position = 0;
                string name = ReadIdentifier(part, ref position);
                if (string.IsNullOrWhiteSpace(name)) return Unsupported(result, "A column identifier could not be parsed.");
                string remainder = part.Substring(position).Trim();
                string declaredType = ReadDeclaredType(remainder);
                string constraints = remainder.Substring(Math.Min(remainder.Length, declaredType.Length)).Trim();
                bool primaryKey = Regex.IsMatch(constraints, @"\bPRIMARY\s+KEY\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                bool unique = Regex.IsMatch(constraints, @"\bUNIQUE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                bool integerPrimary = primaryKey && string.Equals(declaredType.Trim(), "INTEGER", StringComparison.OrdinalIgnoreCase)
                    && !Regex.IsMatch(constraints, @"\bPRIMARY\s+KEY\s+DESC\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                string collation = ReadClauseIdentifier(constraints, "COLLATE") ?? "BINARY";
                SqliteColumnDef column = new SqliteColumnDef
                {
                    Name = name,
                    DeclaredType = declaredType,
                    NotNull = Regex.IsMatch(constraints, @"\bNOT\s+NULL\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                    PrimaryKey = primaryKey,
                    Unique = unique,
                    IntegerPrimaryKey = integerPrimary,
                    AutoIncrement = Regex.IsMatch(constraints, @"\bAUTOINCREMENT\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                    DefaultSql = ReadDefaultExpression(constraints),
                    Collation = collation
                };
                result.Columns.Add(column);
                if (IndexOfWord(constraints, "REFERENCES", 0) >= 0)
                {
                    SqliteForeignKeyDef foreignKey;
                    if (!TryParseForeignKey(constraints, name, out foreignKey))
                        return Unsupported(result, "An inline REFERENCES constraint on column '" + name + "' could not be parsed.");
                    result.ForeignKeys.Add(foreignKey);
                }
                if (integerPrimary)
                {
                    if (result.IntegerPrimaryKeyIndex >= 0) return Unsupported(result, "More than one INTEGER PRIMARY KEY alias was found.");
                    result.IntegerPrimaryKeyIndex = result.Columns.Count - 1;
                }
                else if (primaryKey || unique)
                {
                    pendingKeys.Add(new SqliteUniqueKey
                    {
                        PrimaryKey = primaryKey,
                        Columns = new List<string> { name },
                        Descending = new List<bool> { Regex.IsMatch(constraints, @"\bDESC\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) },
                        Collations = new List<string> { collation }
                    });
                }
            }

            int autoNumber = 1;
            for (int i = 0; i < pendingKeys.Count; i++)
            {
                SqliteUniqueKey key = pendingKeys[i];
                if (key.PrimaryKey && key.Columns.Count == 1)
                {
                    int ci = result.Columns.FindIndex(c => string.Equals(c.Name, key.Columns[0], StringComparison.OrdinalIgnoreCase));
                    if (ci >= 0 && result.Columns[ci].IntegerPrimaryKey) continue;
                }
                key.Name = "sqlite_autoindex_" + tableName + "_" + autoNumber++;
                result.UniqueKeys.Add(key);
                for (int c = 0; c < key.Columns.Count; c++)
                {
                    int ci = result.Columns.FindIndex(x => string.Equals(x.Name, key.Columns[c], StringComparison.OrdinalIgnoreCase));
                    if (ci < 0) return Unsupported(result, "A constraint references unknown column '" + key.Columns[c] + "'.");
                    if (key.PrimaryKey) result.Columns[ci].PrimaryKey = true;
                    else result.Columns[ci].Unique = key.Columns.Count == 1;
                }
            }
            return result;
        }

        public static SqliteIndexDef ParseIndex(SqliteSchemaEntry entry)
        {
            SqliteIndexDef result = new SqliteIndexDef
            {
                Schema = entry,
                AutoIndex = string.IsNullOrWhiteSpace(entry.Sql),
                Supported = true,
                Columns = new List<string>(),
                Descending = new List<bool>(),
                Collations = new List<string>()
            };
            if (result.AutoIndex) return result;
            string sql = entry.Sql;
            result.Unique = Regex.IsMatch(sql, @"^\s*CREATE\s+UNIQUE\s+INDEX\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            int on = IndexOfWord(sql, "ON", 0);
            int open = on < 0 ? -1 : FindCharOutside(sql, '(', on + 2);
            int close = open < 0 ? -1 : FindMatchingParen(sql, open);
            if (open < 0 || close < 0) { result.Supported = false; result.UnsupportedReason = "The CREATE INDEX statement is malformed."; return result; }
            if (IndexOfWord(sql, "WHERE", close + 1) >= 0) { result.Supported = false; result.UnsupportedReason = "Partial indexes are not supported."; return result; }
            List<string> expressions = SplitTopLevel(sql.Substring(open + 1, close - open - 1), ',');
            for (int i = 0; i < expressions.Count; i++)
            {
                string expr = expressions[i].Trim();
                int p = 0;
                string column = ReadIdentifier(expr, ref p);
                string tail = expr.Substring(Math.Min(p, expr.Length)).Trim();
                string collation = ReadClauseIdentifier(tail, "COLLATE") ?? "BINARY";
                string reduced = Regex.Replace(tail, @"\bCOLLATE\s+(?:""[^""]+""|\x60[^\x60]+\x60|\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                reduced = Regex.Replace(reduced, @"\b(ASC|DESC)\b", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
                if (string.IsNullOrWhiteSpace(column) || reduced.Length != 0) { result.Supported = false; result.UnsupportedReason = "Expression indexes are not supported."; return result; }
                if (!IsSupportedCollation(collation)) { result.Supported = false; result.UnsupportedReason = "Only BINARY, NOCASE, and RTRIM collations are supported."; return result; }
                result.Columns.Add(column);
                result.Descending.Add(Regex.IsMatch(tail, @"\bDESC\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
                result.Collations.Add(collation);
            }
            return result;
        }

        private static SqliteDdlResult Unsupported(SqliteDdlResult result, string reason) { result.Writable = false; result.UnsupportedReason = reason; return result; }

        public static List<string> SplitTopLevel(string text, char separator)
        {
            List<string> result = new List<string>();
            int start = 0, depth = 0;
            char quote = '\0';
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (quote != '\0')
                {
                    if (ch == quote)
                    {
                        if (i + 1 < text.Length && text[i + 1] == quote) i++;
                        else quote = '\0';
                    }
                    else if (quote == ']' && ch == ']') quote = '\0';
                    continue;
                }
                if (ch == '\'' || ch == '"' || ch == (char)96) { quote = ch; continue; }
                if (ch == '[') { quote = ']'; continue; }
                if (ch == '(') depth++;
                else if (ch == ')') depth--;
                else if (ch == separator && depth == 0) { result.Add(text.Substring(start, i - start)); start = i + 1; }
            }
            result.Add(text.Substring(start));
            return result;
        }

        public static int FindMatchingParen(string text, int open)
        {
            int depth = 0;
            char quote = '\0';
            for (int i = open; i < text.Length; i++)
            {
                char ch = text[i];
                if (quote != '\0')
                {
                    if (ch == quote)
                    {
                        if (i + 1 < text.Length && text[i + 1] == quote) i++;
                        else quote = '\0';
                    }
                    else if (quote == ']' && ch == ']') quote = '\0';
                    continue;
                }
                if (ch == '\'' || ch == '"' || ch == (char)96) { quote = ch; continue; }
                if (ch == '[') { quote = ']'; continue; }
                if (ch == '(') depth++;
                else if (ch == ')' && --depth == 0) return i;
            }
            return -1;
        }

        public static int FindCharOutside(string text, char target, int start)
        {
            char quote = '\0';
            for (int i = start; i < text.Length; i++)
            {
                char ch = text[i];
                if (quote != '\0')
                {
                    if (ch == quote)
                    {
                        if (i + 1 < text.Length && text[i + 1] == quote) i++;
                        else quote = '\0';
                    }
                    else if (quote == ']' && ch == ']') quote = '\0';
                    continue;
                }
                if (ch == '\'' || ch == '"' || ch == (char)96) { quote = ch; continue; }
                if (ch == '[') { quote = ']'; continue; }
                if (ch == target) return i;
            }
            return -1;
        }

        public static int IndexOfWord(string text, string word, int start)
        {
            int depth = 0;
            char quote = '\0';
            for (int i = start; i <= text.Length - word.Length; i++)
            {
                char ch = text[i];
                if (quote != '\0')
                {
                    if (ch == quote)
                    {
                        if (i + 1 < text.Length && text[i + 1] == quote) i++;
                        else quote = '\0';
                    }
                    else if (quote == ']' && ch == ']') quote = '\0';
                    continue;
                }
                if (ch == '\'' || ch == '"' || ch == (char)96) { quote = ch; continue; }
                if (ch == '[') { quote = ']'; continue; }
                if (ch == '(') { depth++; continue; }
                if (ch == ')') { depth--; continue; }
                if (depth == 0 && string.Compare(text, i, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0
                    && (i == 0 || !IsIdent(text[i - 1])) && (i + word.Length == text.Length || !IsIdent(text[i + word.Length]))) return i;
            }
            return -1;
        }

        public static string ReadIdentifier(string text, ref int position)
        {
            while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
            if (position >= text.Length) return null;
            char open = text[position];
            if (open == '"' || open == (char)96 || open == '[')
            {
                char close = open == '[' ? ']' : open;
                position++;
                StringBuilder value = new StringBuilder();
                while (position < text.Length)
                {
                    char ch = text[position++];
                    if (ch == close)
                    {
                        if (position < text.Length && text[position] == close && close != ']') { value.Append(close); position++; continue; }
                        return value.ToString();
                    }
                    value.Append(ch);
                }
                return null;
            }
            int start = position;
            while (position < text.Length && IsIdent(text[position])) position++;
            return position == start ? null : text.Substring(start, position - start);
        }

        private static string ReadDeclaredType(string remainder)
        {
            int best = remainder.Length;
            for (int i = 0; i < ColumnConstraintWords.Length; i++)
            {
                int found = IndexOfWord(remainder, ColumnConstraintWords[i], 0);
                if (found >= 0 && found < best) best = found;
            }
            return remainder.Substring(0, best).Trim();
        }

        private static string StripConstraintPrefix(string part)
        {
            if (!StartsWord(part, "CONSTRAINT")) return part.TrimStart();
            int p = "CONSTRAINT".Length;
            ReadIdentifier(part, ref p);
            return part.Substring(Math.Min(p, part.Length)).TrimStart();
        }

        private static bool StartsWord(string text, string word)
        {
            text = text.TrimStart();
            return text.Length >= word.Length && string.Compare(text, 0, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0
                && (text.Length == word.Length || !IsIdent(text[word.Length]));
        }

        private static bool TryParseIndexedColumns(string text, out List<string> columns, out List<bool> descending, out List<string> collations)
        {
            columns = new List<string>(); descending = new List<bool>(); collations = new List<string>();
            int open = FindCharOutside(text, '(', 0);
            int close = open < 0 ? -1 : FindMatchingParen(text, open);
            if (open < 0 || close < 0) return false;
            List<string> parts = SplitTopLevel(text.Substring(open + 1, close - open - 1), ',');
            for (int i = 0; i < parts.Count; i++)
            {
                int p = 0;
                string column = ReadIdentifier(parts[i], ref p);
                string tail = parts[i].Substring(Math.Min(p, parts[i].Length));
                string collation = ReadClauseIdentifier(tail, "COLLATE") ?? "BINARY";
                string reduced = Regex.Replace(tail, @"\bCOLLATE\s+(?:""[^""]+""|\x60[^\x60]+\x60|\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                reduced = Regex.Replace(reduced, @"\b(ASC|DESC)\b", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
                if (column == null || reduced.Length != 0 || !IsSupportedCollation(collation)) return false;
                columns.Add(column);
                descending.Add(Regex.IsMatch(tail, @"\bDESC\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
                collations.Add(collation);
            }
            return columns.Count > 0;
        }

        private static bool TryParseForeignKey(string text, string inlineChildColumn, out SqliteForeignKeyDef foreignKey)
        {
            foreignKey = null;
            List<string> childColumns = new List<string>();
            int references = IndexOfWord(text, "REFERENCES", 0);
            if (references < 0) return false;
            if (inlineChildColumn != null) childColumns.Add(inlineChildColumn);
            else
            {
                int key = IndexOfWord(text, "KEY", 0);
                int open = key < 0 ? -1 : FindCharOutside(text, '(', key + 3);
                int close = open < 0 ? -1 : FindMatchingParen(text, open);
                if (open < 0 || close < 0 || close > references) return false;
                List<string> parts = SplitTopLevel(text.Substring(open + 1, close - open - 1), ',');
                for (int i = 0; i < parts.Count; i++)
                {
                    int p = 0;
                    string column = ReadIdentifier(parts[i], ref p);
                    if (string.IsNullOrWhiteSpace(column) || parts[i].Substring(Math.Min(p, parts[i].Length)).Trim().Length != 0) return false;
                    childColumns.Add(column);
                }
            }

            int position = references + "REFERENCES".Length;
            string parentTable = ReadIdentifier(text, ref position);
            if (string.IsNullOrWhiteSpace(parentTable)) return false;
            while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
            if (position < text.Length && text[position] == '.') return false;

            List<string> parentColumns = new List<string>();
            if (position < text.Length && text[position] == '(')
            {
                int close = FindMatchingParen(text, position);
                if (close < 0) return false;
                List<string> parts = SplitTopLevel(text.Substring(position + 1, close - position - 1), ',');
                for (int i = 0; i < parts.Count; i++)
                {
                    int p = 0;
                    string column = ReadIdentifier(parts[i], ref p);
                    if (string.IsNullOrWhiteSpace(column) || parts[i].Substring(Math.Min(p, parts[i].Length)).Trim().Length != 0) return false;
                    parentColumns.Add(column);
                }
            }
            if (childColumns.Count == 0 || (parentColumns.Count > 0 && parentColumns.Count != childColumns.Count)) return false;
            foreignKey = new SqliteForeignKeyDef { ChildColumns = childColumns, ParentTable = parentTable, ParentColumns = parentColumns };
            return true;
        }

        private static string ReadClauseIdentifier(string text, string keyword)
        {
            int found = IndexOfWord(text, keyword, 0);
            if (found < 0) return null;
            int p = found + keyword.Length;
            return ReadIdentifier(text, ref p);
        }

        private static string ReadDefaultExpression(string constraints)
        {
            int found = IndexOfWord(constraints, "DEFAULT", 0);
            if (found < 0) return null;
            int p = found + 7;
            while (p < constraints.Length && char.IsWhiteSpace(constraints[p])) p++;
            if (p >= constraints.Length) return null;
            if (constraints[p] == '(')
            {
                int end = FindMatchingParen(constraints, p);
                return end < 0 ? null : constraints.Substring(p, end - p + 1);
            }
            if (constraints[p] == '\'')
            {
                int start = p++;
                while (p < constraints.Length)
                {
                    if (constraints[p++] != '\'') continue;
                    if (p < constraints.Length && constraints[p] == '\'') { p++; continue; }
                    return constraints.Substring(start, p - start);
                }
                return null;
            }
            if (constraints[p] == '"' || constraints[p] == (char)96 || constraints[p] == '[')
            {
                int start = p;
                ReadIdentifier(constraints, ref p);
                return constraints.Substring(start, p - start);
            }
            int endPos = p;
            while (endPos < constraints.Length && !char.IsWhiteSpace(constraints[endPos]) && constraints[endPos] != ',') endPos++;
            return constraints.Substring(p, endPos - p);
        }

        private static bool IsSupportedCollation(string value)
        {
            return string.Equals(value, "BINARY", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "NOCASE", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "RTRIM", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsIdent(char ch) { return char.IsLetterOrDigit(ch) || ch == '_' || ch == '$'; }
    }


    // ==================== SQLite serializer ====================

    private sealed class SqliteWriter
    {
        private readonly SqliteDatabase _database;
        private readonly Action _checkBudget;
        private readonly List<byte[]> _pages;
        private readonly int _pageSize;
        private readonly int _usable;

        public SqliteWriter(SqliteDatabase database, Action checkBudget)
        {
            _database = database;
            _checkBudget = checkBudget;
            _pageSize = database.PageSize;
            _usable = _pageSize;
            _pages = new List<byte[]> { new byte[_pageSize] };
        }

        public byte[] Write()
        {
            _database.EnsureWritable();
            foreach (SqliteSchemaEntry entry in _database.SchemaEntries) entry.OutputRootPage = 0;

            foreach (SqliteTable table in _database.Tables.Values.OrderBy(t => t.Schema.RowId))
            {
                int root = AllocatePage();
                table.Schema.OutputRootPage = root;
                BuildTableBtree(root, table.Rows.Select(r =>
                {
                    object[] stored = new object[r.Values.Length];
                    Array.Copy(r.Values, stored, stored.Length);
                    if (table.IntegerPrimaryKeyIndex >= 0) stored[table.IntegerPrimaryKeyIndex] = null;
                    return new TableEntry { Key = r.RowId, Payload = EncodeRecord(stored) };
                }).ToList());
            }

            foreach (SqliteIndexDef index in _database.Indexes.OrderBy(i => i.Schema.RowId))
            {
                int root = AllocatePage();
                index.Schema.OutputRootPage = root;
                SqliteTable table = _database.Tables[index.Schema.TableName];
                BuildIndexBtree(root, BuildIndexEntries(table, index), index);
            }

            List<TableEntry> master = new List<TableEntry>();
            foreach (SqliteSchemaEntry entry in _database.SchemaEntries.OrderBy(e => e.RowId))
            {
                int root = string.Equals(entry.Type, "table", StringComparison.OrdinalIgnoreCase) || string.Equals(entry.Type, "index", StringComparison.OrdinalIgnoreCase)
                    ? entry.OutputRootPage : 0;
                master.Add(new TableEntry
                {
                    Key = entry.RowId,
                    Payload = EncodeRecord(new object[] { entry.Type, entry.Name, entry.TableName, (long)root, entry.Sql })
                });
            }
            BuildTableBtree(1, master);
            WriteDatabaseHeader();

            long outputSize = (long)_pages.Count * _pageSize;
            if (_pages.Count > _database.Limits.MaxPages || outputSize > _database.Limits.MaxDatabaseBytes)
                throw new SqliteConnectorException(HttpStatusCode.RequestEntityTooLarge, "OUTPUT_LIMIT_EXCEEDED", "The rebuilt database exceeds the configured output limits.", new JObject { ["pageCount"] = _pages.Count, ["sizeBytes"] = outputSize });

            byte[] output = new byte[outputSize];
            for (int i = 0; i < _pages.Count; i++) Buffer.BlockCopy(_pages[i], 0, output, i * _pageSize, _pageSize);
            return output;
        }

        private sealed class TableEntry
        {
            public long Key { get; set; }
            public byte[] Payload { get; set; }
            public byte[] Cell { get; set; }
        }

        private sealed class TableChild
        {
            public int Page { get; set; }
            public long MaxKey { get; set; }
        }

        private sealed class IndexEntry
        {
            public object[] Values { get; set; }
            public byte[] Payload { get; set; }
        }

        private sealed class IndexNode
        {
            public List<IndexEntry> Entries { get; set; }
            public List<IndexNode> Children { get; set; }
            public int Page { get; set; }
            public bool IsLeaf { get { return Children == null || Children.Count == 0; } }
        }

        private List<IndexEntry> BuildIndexEntries(SqliteTable table, SqliteIndexDef index)
        {
            List<IndexEntry> result = new List<IndexEntry>(table.Rows.Count);
            for (int r = 0; r < table.Rows.Count; r++)
            {
                object[] values = new object[index.Columns.Count + 1];
                for (int c = 0; c < index.Columns.Count; c++)
                {
                    int ci = table.FindColumn(index.Columns[c]);
                    if (ci < 0) throw new SqliteConnectorException(HttpStatusCode.BadRequest, "UNSUPPORTED_INDEX", "Index '" + index.Schema.Name + "' references unknown column '" + index.Columns[c] + "'.", null);
                    values[c] = table.Rows[r].Values[ci];
                }
                values[values.Length - 1] = table.Rows[r].RowId;
                result.Add(new IndexEntry { Values = values, Payload = EncodeRecord(values) });
            }
            result.Sort((a, b) => CompareIndexEntries(a, b, index));
            return result;
        }

        private int CompareIndexEntries(IndexEntry left, IndexEntry right, SqliteIndexDef index)
        {
            for (int i = 0; i < index.Columns.Count; i++)
            {
                int compare = CompareIndexValue(left.Values[i], right.Values[i], index.Collations[i]);
                if (compare != 0) return index.Descending[i] ? -compare : compare;
            }
            return CompareIndexValue(left.Values[left.Values.Length - 1], right.Values[right.Values.Length - 1], "BINARY");
        }

        private static int CompareIndexValue(object left, object right, string collation)
        {
            if (left == null && right == null) return 0;
            if (left == null) return -1;
            if (right == null) return 1;
            if (left is DateTime) left = ((DateTime)left).ToString("o");
            if (right is DateTime) right = ((DateTime)right).ToString("o");
            bool leftNumber = IsNumber(left), rightNumber = IsNumber(right);
            if (leftNumber && rightNumber)
            {
                if (IsIntegerNumber(left) && IsIntegerNumber(right))
                    return Convert.ToInt64(left).CompareTo(Convert.ToInt64(right));
                return ToSqliteDouble(left).CompareTo(ToSqliteDouble(right));
            }
            if (leftNumber != rightNumber) return leftNumber ? -1 : 1;
            byte[] lb = left as byte[];
            byte[] rb = right as byte[];
            if (lb != null || rb != null)
            {
                if (lb == null) return -1;
                if (rb == null) return 1;
                int common = Math.Min(lb.Length, rb.Length);
                for (int i = 0; i < common; i++) if (lb[i] != rb[i]) return lb[i] < rb[i] ? -1 : 1;
                return lb.Length.CompareTo(rb.Length);
            }
            string ls = left.ToString(), rs = right.ToString();
            if (string.Equals(collation, "NOCASE", StringComparison.OrdinalIgnoreCase)) return string.Compare(FoldAsciiNoCase(ls), FoldAsciiNoCase(rs), StringComparison.Ordinal);
            if (string.Equals(collation, "RTRIM", StringComparison.OrdinalIgnoreCase)) return string.Compare(ls.TrimEnd(' '), rs.TrimEnd(' '), StringComparison.Ordinal);
            return string.Compare(ls, rs, StringComparison.Ordinal);
        }

        private static string FoldAsciiNoCase(string value)
        {
            char[] chars = value.ToCharArray();
            for (int i = 0; i < chars.Length; i++) if (chars[i] >= 'A' && chars[i] <= 'Z') chars[i] = (char)(chars[i] + 32);
            return new string(chars);
        }

        private static bool IsNumber(object value)
        {
            return value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint || value is long || value is ulong || value is float || value is double || value is decimal || value is bool;
        }

        private static bool IsIntegerNumber(object value)
        {
            return value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint || value is long || value is bool;
        }

        private static double ToSqliteDouble(object value)
        {
            if (value is bool) return (bool)value ? 1d : 0d;
            return Convert.ToDouble(value);
        }

        private void BuildTableBtree(int rootPage, List<TableEntry> entries)
        {
            entries.Sort((a, b) => a.Key.CompareTo(b.Key));
            for (int i = 0; i < entries.Count; i++) entries[i].Cell = MakeTableLeafCell(entries[i].Key, entries[i].Payload);
            int rootHeader = rootPage == 1 ? 100 : 0;
            if (FitsCells(entries.Select(e => e.Cell).ToList(), 8, rootHeader))
            {
                WriteLeafPage(rootPage, 13, entries.Select(e => e.Cell).ToList(), rootHeader);
                return;
            }

            List<List<TableEntry>> groups = GroupTableLeafEntries(entries);
            List<TableChild> level = new List<TableChild>();
            for (int i = 0; i < groups.Count; i++)
            {
                int page = AllocatePage();
                WriteLeafPage(page, 13, groups[i].Select(e => e.Cell).ToList(), 0);
                level.Add(new TableChild { Page = page, MaxKey = groups[i][groups[i].Count - 1].Key });
            }
            while (!FitsTableInterior(level, rootHeader))
            {
                List<TableChild> next = new List<TableChild>();
                List<List<TableChild>> parentGroups = GroupTableChildren(level, 0);
                for (int i = 0; i < parentGroups.Count; i++)
                {
                    int page = AllocatePage();
                    WriteTableInteriorPage(page, parentGroups[i], 0);
                    next.Add(new TableChild { Page = page, MaxKey = parentGroups[i][parentGroups[i].Count - 1].MaxKey });
                }
                level = next;
            }
            WriteTableInteriorPage(rootPage, level, rootHeader);
        }

        private List<List<TableEntry>> GroupTableLeafEntries(List<TableEntry> entries)
        {
            List<List<TableEntry>> groups = new List<List<TableEntry>>();
            List<TableEntry> current = new List<TableEntry>();
            int used = 8;
            for (int i = 0; i < entries.Count; i++)
            {
                int need = 2 + entries[i].Cell.Length;
                if (current.Count > 0 && used + need > _usable)
                {
                    groups.Add(current);
                    current = new List<TableEntry>();
                    used = 8;
                }
                if (used + need > _usable) throw new SqliteConnectorException(HttpStatusCode.BadRequest, "CELL_TOO_LARGE", "A table cell cannot fit on a SQLite page after overflow handling.", null);
                current.Add(entries[i]);
                used += need;
            }
            if (current.Count > 0) groups.Add(current);
            return groups;
        }

        private List<List<TableChild>> GroupTableChildren(List<TableChild> children, int headerOffset)
        {
            List<List<TableChild>> groups = new List<List<TableChild>>();
            List<TableChild> current = new List<TableChild>();
            int used = headerOffset + 12;
            for (int i = 0; i < children.Count; i++)
            {
                int need = current.Count == 0 ? 0 : 2 + 4 + EncodeVarint(unchecked((ulong)current[current.Count - 1].MaxKey)).Length;
                if (current.Count >= 2 && used + need > _usable)
                {
                    groups.Add(current);
                    current = new List<TableChild>();
                    used = headerOffset + 12;
                    need = 0;
                }
                current.Add(children[i]);
                used += need;
            }
            if (current.Count > 0) groups.Add(current);
            if (groups.Any(g => g.Count < 2)) throw new SqliteConnectorException(HttpStatusCode.InternalServerError, "BTREE_BUILD_FAILED", "A table interior page could not be balanced.", null);
            return groups;
        }

        private bool FitsTableInterior(List<TableChild> children, int headerOffset)
        {
            int used = headerOffset + 12;
            for (int i = 0; i < children.Count - 1; i++) used += 2 + 4 + EncodeVarint(unchecked((ulong)children[i].MaxKey)).Length;
            return children.Count >= 2 && used <= _usable;
        }

        private void WriteTableInteriorPage(int page, List<TableChild> children, int headerOffset)
        {
            List<byte[]> cells = new List<byte[]>();
            for (int i = 0; i < children.Count - 1; i++)
            {
                byte[] key = EncodeVarint(unchecked((ulong)children[i].MaxKey));
                byte[] cell = new byte[4 + key.Length];
                WriteUInt32(cell, 0, (uint)children[i].Page);
                Buffer.BlockCopy(key, 0, cell, 4, key.Length);
                cells.Add(cell);
            }
            WriteBtreePage(page, 5, cells, headerOffset, children[children.Count - 1].Page);
        }

        private void BuildIndexBtree(int rootPage, List<IndexEntry> entries, SqliteIndexDef index)
        {
            if (FitsIndexLeaf(entries, 0))
            {
                WriteIndexNode(new IndexNode { Entries = entries, Children = null }, rootPage);
                return;
            }
            List<IndexNode> nodes;
            List<IndexEntry> separators;
            BuildLeafLevel(entries, out nodes, out separators);
            while (!FitsIndexInterior(separators, 0))
            {
                List<IndexNode> parentNodes;
                List<IndexEntry> promoted;
                BuildInteriorLevel(nodes, separators, out parentNodes, out promoted);
                nodes = parentNodes;
                separators = promoted;
            }
            IndexNode root = new IndexNode { Entries = separators, Children = nodes };
            WriteIndexNode(root, rootPage);
        }

        private void BuildLeafLevel(List<IndexEntry> entries, out List<IndexNode> nodes, out List<IndexEntry> separators)
        {
            nodes = new List<IndexNode>();
            separators = new List<IndexEntry>();
            List<IndexEntry> current = new List<IndexEntry>();
            int used = 8;
            for (int i = 0; i < entries.Count; i++)
            {
                int need = 2 + IndexCellSize(entries[i].Payload.Length, false);
                if (current.Count > 0 && used + need > _usable)
                {
                    if (current.Count < 2) throw new SqliteConnectorException(HttpStatusCode.BadRequest, "INDEX_CELL_TOO_LARGE", "An index page cannot be balanced.", null);
                    IndexEntry promoted = current[current.Count - 1];
                    current.RemoveAt(current.Count - 1);
                    nodes.Add(new IndexNode { Entries = current, Children = null });
                    separators.Add(promoted);
                    current = new List<IndexEntry>();
                    used = 8;
                }
                current.Add(entries[i]);
                used += need;
            }
            if (current.Count == 0 && separators.Count > 0)
            {
                current.Add(separators[separators.Count - 1]);
                separators.RemoveAt(separators.Count - 1);
            }
            nodes.Add(new IndexNode { Entries = current, Children = null });
        }

        private void BuildInteriorLevel(List<IndexNode> children, List<IndexEntry> separators, out List<IndexNode> parents, out List<IndexEntry> promoted)
        {
            parents = new List<IndexNode>();
            promoted = new List<IndexEntry>();
            int childStart = 0;
            int separatorStart = 0;
            int used = 12;
            for (int i = 0; i < separators.Count; i++)
            {
                int need = 2 + IndexCellSize(separators[i].Payload.Length, true);
                int cellsInGroup = i - separatorStart;
                if (cellsInGroup > 0 && used + need > _usable)
                {
                    parents.Add(new IndexNode
                    {
                        Entries = separators.GetRange(separatorStart, cellsInGroup),
                        Children = children.GetRange(childStart, cellsInGroup + 1)
                    });
                    promoted.Add(separators[i]);
                    childStart = i + 1;
                    separatorStart = i + 1;
                    used = 12;
                }
                else used += need;
            }
            int remainingCells = separators.Count - separatorStart;
            parents.Add(new IndexNode
            {
                Entries = separators.GetRange(separatorStart, remainingCells),
                Children = children.GetRange(childStart, remainingCells + 1)
            });
            if (parents.Any(p => p.Entries.Count == 0) && parents.Count > 1)
                throw new SqliteConnectorException(HttpStatusCode.InternalServerError, "BTREE_BUILD_FAILED", "An index interior page could not be balanced.", null);
        }

        private bool FitsIndexLeaf(List<IndexEntry> entries, int headerOffset)
        {
            int used = headerOffset + 8;
            for (int i = 0; i < entries.Count; i++) used += 2 + IndexCellSize(entries[i].Payload.Length, false);
            return used <= _usable;
        }

        private bool FitsIndexInterior(List<IndexEntry> entries, int headerOffset)
        {
            int used = headerOffset + 12;
            for (int i = 0; i < entries.Count; i++) used += 2 + IndexCellSize(entries[i].Payload.Length, true);
            return used <= _usable;
        }

        private int IndexCellSize(int payloadLength, bool interior)
        {
            int local = LocalPayloadSize(payloadLength, false);
            return (interior ? 4 : 0) + EncodeVarint((ulong)payloadLength).Length + local + (local < payloadLength ? 4 : 0);
        }

        private int WriteIndexNode(IndexNode node, int requestedPage)
        {
            int page = requestedPage > 0 ? requestedPage : AllocatePage();
            node.Page = page;
            List<int> childPages = new List<int>();
            if (!node.IsLeaf)
                for (int i = 0; i < node.Children.Count; i++) childPages.Add(WriteIndexNode(node.Children[i], 0));
            List<byte[]> cells = new List<byte[]>();
            for (int i = 0; i < node.Entries.Count; i++) cells.Add(MakeIndexCell(node.Entries[i].Payload, node.IsLeaf ? 0 : childPages[i]));
            WriteBtreePage(page, node.IsLeaf ? 10 : 2, cells, 0, node.IsLeaf ? 0 : childPages[childPages.Count - 1]);
            return page;
        }

        private byte[] MakeTableLeafCell(long key, byte[] payload)
        {
            byte[] payloadLength = EncodeVarint((ulong)payload.Length);
            byte[] rowId = EncodeVarint(unchecked((ulong)key));
            int local = LocalPayloadSize(payload.Length, true);
            int overflow = local < payload.Length ? WriteOverflow(payload, local) : 0;
            byte[] cell = new byte[payloadLength.Length + rowId.Length + local + (overflow == 0 ? 0 : 4)];
            int p = 0;
            Buffer.BlockCopy(payloadLength, 0, cell, p, payloadLength.Length); p += payloadLength.Length;
            Buffer.BlockCopy(rowId, 0, cell, p, rowId.Length); p += rowId.Length;
            Buffer.BlockCopy(payload, 0, cell, p, local); p += local;
            if (overflow != 0) WriteUInt32(cell, p, (uint)overflow);
            return cell;
        }

        private byte[] MakeIndexCell(byte[] payload, int leftChild)
        {
            byte[] length = EncodeVarint((ulong)payload.Length);
            int local = LocalPayloadSize(payload.Length, false);
            int overflow = local < payload.Length ? WriteOverflow(payload, local) : 0;
            byte[] cell = new byte[(leftChild == 0 ? 0 : 4) + length.Length + local + (overflow == 0 ? 0 : 4)];
            int p = 0;
            if (leftChild != 0) { WriteUInt32(cell, p, (uint)leftChild); p += 4; }
            Buffer.BlockCopy(length, 0, cell, p, length.Length); p += length.Length;
            Buffer.BlockCopy(payload, 0, cell, p, local); p += local;
            if (overflow != 0) WriteUInt32(cell, p, (uint)overflow);
            return cell;
        }

        private int WriteOverflow(byte[] payload, int offset)
        {
            int first = 0, previous = 0;
            while (offset < payload.Length)
            {
                int page = AllocatePage();
                if (first == 0) first = page;
                if (previous != 0) WriteUInt32(_pages[previous - 1], 0, (uint)page);
                int count = Math.Min(_usable - 4, payload.Length - offset);
                Buffer.BlockCopy(payload, offset, _pages[page - 1], 4, count);
                offset += count;
                previous = page;
            }
            return first;
        }

        private int LocalPayloadSize(int payloadLength, bool tableLeaf)
        {
            int maxLocal = tableLeaf ? _usable - 35 : ((_usable - 12) * 64 / 255) - 23;
            if (payloadLength <= maxLocal) return payloadLength;
            int minLocal = ((_usable - 12) * 32 / 255) - 23;
            int local = minLocal + (payloadLength - minLocal) % (_usable - 4);
            return local <= maxLocal ? local : minLocal;
        }

        private bool FitsCells(List<byte[]> cells, int headerSize, int headerOffset)
        {
            return headerOffset + headerSize + cells.Count * 2 + cells.Sum(c => c.Length) <= _usable;
        }

        private void WriteLeafPage(int page, int type, List<byte[]> cells, int headerOffset) { WriteBtreePage(page, type, cells, headerOffset, 0); }

        private void WriteBtreePage(int page, int type, List<byte[]> cells, int headerOffset, int rightmost)
        {
            byte[] data = _pages[page - 1];
            Array.Clear(data, headerOffset, data.Length - headerOffset);
            int headerSize = type == 2 || type == 5 ? 12 : 8;
            data[headerOffset] = (byte)type;
            WriteUInt16(data, headerOffset + 1, 0);
            WriteUInt16(data, headerOffset + 3, cells.Count);
            data[headerOffset + 7] = 0;
            if (headerSize == 12) WriteUInt32(data, headerOffset + 8, (uint)rightmost);
            int content = _usable;
            for (int i = 0; i < cells.Count; i++)
            {
                content -= cells[i].Length;
                if (content < headerOffset + headerSize + cells.Count * 2)
                    throw new SqliteConnectorException(HttpStatusCode.InternalServerError, "BTREE_BUILD_FAILED", "A generated b-tree page overflowed.", null);
                Buffer.BlockCopy(cells[i], 0, data, content, cells[i].Length);
                WriteUInt16(data, headerOffset + headerSize + i * 2, content == 65536 ? 0 : content);
            }
            WriteUInt16(data, headerOffset + 5, content == 65536 ? 0 : content);
        }

        private int AllocatePage()
        {
            _checkBudget();
            if (_pages.Count >= _database.Limits.MaxPages)
                throw new SqliteConnectorException(HttpStatusCode.RequestEntityTooLarge, "PAGE_LIMIT_EXCEEDED", "The rebuilt database exceeds maxPages.", null);
            _pages.Add(new byte[_pageSize]);
            return _pages.Count;
        }

        private byte[] EncodeRecord(object[] values)
        {
            List<byte[]> serials = new List<byte[]>();
            List<byte[]> bodies = new List<byte[]>();
            for (int i = 0; i < values.Length; i++)
            {
                ulong serial;
                byte[] body;
                EncodeValue(values[i], out serial, out body);
                serials.Add(EncodeVarint(serial));
                bodies.Add(body);
            }
            int serialBytes = serials.Sum(x => x.Length);
            int headerLength = serialBytes + 1;
            while (EncodeVarint((ulong)headerLength).Length + serialBytes != headerLength)
                headerLength = EncodeVarint((ulong)headerLength).Length + serialBytes;
            byte[] headerVarint = EncodeVarint((ulong)headerLength);
            int bodyLength = bodies.Sum(x => x.Length);
            byte[] result = new byte[headerLength + bodyLength];
            int p = 0;
            Buffer.BlockCopy(headerVarint, 0, result, p, headerVarint.Length); p += headerVarint.Length;
            for (int i = 0; i < serials.Count; i++) { Buffer.BlockCopy(serials[i], 0, result, p, serials[i].Length); p += serials[i].Length; }
            p = headerLength;
            for (int i = 0; i < bodies.Count; i++) { Buffer.BlockCopy(bodies[i], 0, result, p, bodies[i].Length); p += bodies[i].Length; }
            return result;
        }

        private void EncodeValue(object value, out ulong serial, out byte[] body)
        {
            if (value == null) { serial = 0; body = new byte[0]; return; }
            if (value is bool) value = (bool)value ? 1L : 0L;
            if (value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint || value is long)
            {
                long number = Convert.ToInt64(value);
                if (number == 0) { serial = 8; body = new byte[0]; return; }
                if (number == 1) { serial = 9; body = new byte[0]; return; }
                int size;
                if (number >= -128 && number <= 127) { serial = 1; size = 1; }
                else if (number >= -32768 && number <= 32767) { serial = 2; size = 2; }
                else if (number >= -8388608 && number <= 8388607) { serial = 3; size = 3; }
                else if (number >= int.MinValue && number <= int.MaxValue) { serial = 4; size = 4; }
                else if (number >= -140737488355328L && number <= 140737488355327L) { serial = 5; size = 6; }
                else { serial = 6; size = 8; }
                body = EncodeSigned(number, size);
                return;
            }
            if (value is float || value is double || value is decimal)
            {
                serial = 7;
                byte[] little = BitConverter.GetBytes(Convert.ToDouble(value));
                body = new byte[8];
                for (int i = 0; i < 8; i++) body[i] = little[7 - i];
                return;
            }
            byte[] blob = value as byte[];
            if (blob != null) { serial = 12UL + (ulong)blob.Length * 2UL; body = blob; return; }
            string text = value is DateTime ? ((DateTime)value).ToString("o") : value.ToString();
            body = _database.TextEncoding.GetBytes(text);
            serial = 13UL + (ulong)body.Length * 2UL;
        }

        private static byte[] EncodeSigned(long value, int size)
        {
            byte[] result = new byte[size];
            ulong bits = unchecked((ulong)value);
            for (int i = size - 1; i >= 0; i--) { result[i] = (byte)(bits & 0xff); bits >>= 8; }
            return result;
        }

        private static byte[] EncodeVarint(ulong value)
        {
            byte[] buffer = new byte[9];
            if ((value & 0xff00000000000000UL) != 0)
            {
                buffer[8] = (byte)value;
                value >>= 8;
                for (int i = 7; i >= 0; i--) { buffer[i] = (byte)((value & 0x7f) | 0x80); value >>= 7; }
                return buffer;
            }
            int start = 8;
            buffer[start] = (byte)(value & 0x7f);
            while ((value >>= 7) != 0) buffer[--start] = (byte)((value & 0x7f) | 0x80);
            byte[] result = new byte[9 - start];
            Buffer.BlockCopy(buffer, start, result, 0, result.Length);
            return result;
        }

        private void WriteDatabaseHeader()
        {
            byte[] page = _pages[0];
            byte[] magic = Encoding.ASCII.GetBytes("SQLite format 3\0");
            Buffer.BlockCopy(magic, 0, page, 0, magic.Length);
            WriteUInt16(page, 16, _pageSize == 65536 ? 1 : _pageSize);
            page[18] = 1; page[19] = 1; page[20] = 0; page[21] = 64; page[22] = 32; page[23] = 32;
            uint change = _database.ChangeCounter == uint.MaxValue ? 1U : _database.ChangeCounter + 1U;
            WriteUInt32(page, 24, change);
            WriteUInt32(page, 28, (uint)_pages.Count);
            WriteUInt32(page, 32, 0); WriteUInt32(page, 36, 0);
            WriteUInt32(page, 40, _database.SchemaChanged ? _database.SchemaCookie + 1U : _database.SchemaCookie);
            WriteUInt32(page, 44, 4);
            WriteUInt32(page, 48, _database.DefaultCacheSize);
            WriteUInt32(page, 52, 0);
            WriteUInt32(page, 56, (uint)_database.EncodingId);
            WriteUInt32(page, 60, _database.UserVersion);
            WriteUInt32(page, 64, 0);
            WriteUInt32(page, 68, _database.ApplicationId);
            for (int i = 72; i < 92; i++) page[i] = 0;
            WriteUInt32(page, 92, change);
            WriteUInt32(page, 96, _database.WriteLibraryVersion == 0 ? 3046000U : _database.WriteLibraryVersion);
        }

        private static void WriteUInt16(byte[] bytes, int offset, int value)
        {
            bytes[offset] = (byte)((value >> 8) & 0xff);
            bytes[offset + 1] = (byte)(value & 0xff);
        }

        private static void WriteUInt32(byte[] bytes, int offset, uint value)
        {
            bytes[offset] = (byte)(value >> 24);
            bytes[offset + 1] = (byte)(value >> 16);
            bytes[offset + 2] = (byte)(value >> 8);
            bytes[offset + 3] = (byte)value;
        }
    }


    private static string BindParameters(string sql, JObject parameters)
    {
        if (parameters == null || parameters.Count == 0) return sql;
        StringBuilder output = new StringBuilder(sql.Length + 64);
        char quote = '\0';
        bool lineComment = false, blockComment = false;
        for (int i = 0; i < sql.Length; i++)
        {
            char ch = sql[i];
            if (lineComment)
            {
                output.Append(ch);
                if (ch == '\n' || ch == '\r') lineComment = false;
                continue;
            }
            if (blockComment)
            {
                output.Append(ch);
                if (ch == '*' && i + 1 < sql.Length && sql[i + 1] == '/') { output.Append('/'); i++; blockComment = false; }
                continue;
            }
            if (quote != '\0')
            {
                output.Append(ch);
                if (ch == quote)
                {
                    if (i + 1 < sql.Length && sql[i + 1] == quote) { output.Append(sql[++i]); }
                    else quote = '\0';
                }
                else if (quote == ']' && ch == ']') quote = '\0';
                continue;
            }
            if (ch == '-' && i + 1 < sql.Length && sql[i + 1] == '-') { output.Append("--"); i++; lineComment = true; continue; }
            if (ch == '/' && i + 1 < sql.Length && sql[i + 1] == '*') { output.Append("/*"); i++; blockComment = true; continue; }
            if (ch == '\'' || ch == '"' || ch == (char)96) { quote = ch; output.Append(ch); continue; }
            if (ch == '[') { quote = ']'; output.Append(ch); continue; }
            if (ch == '@' || ch == ':' || ch == '$')
            {
                int start = i + 1, end = start;
                while (end < sql.Length && (char.IsLetterOrDigit(sql[end]) || sql[end] == '_')) end++;
                if (end > start)
                {
                    string name = sql.Substring(start, end - start);
                    JToken value;
                    if (!parameters.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out value))
                        throw new DatasetException("MISSING_SQL_PARAMETER", "No value was supplied for SQL parameter '" + name + "'.", null);
                    output.Append(ToSqlLiteral(value));
                    i = end - 1;
                    continue;
                }
            }
            output.Append(ch);
        }
        return output.ToString();
    }

    private static string ToSqlLiteral(JToken token)
    {
        if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined) return "NULL";
        if (token.Type == JTokenType.Boolean) return token.ToObject<bool>() ? "1" : "0";
        if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) return token.ToString(Newtonsoft.Json.Formatting.None);
        if (token.Type == JTokenType.Date) return "'" + token.ToObject<DateTime>().ToString("o").Replace("'", "''") + "'";
        if (token.Type == JTokenType.String) return "'" + token.ToString().Replace("'", "''") + "'";
        throw new DatasetException("UNSUPPORTED_SQL_PARAMETER", "SQL parameters must be scalar JSON values.", null);
    }


    private sealed class SqliteChangeSummary
    {
        public int Statements { get; set; }
        public int Inserted { get; set; }
        public int Updated { get; set; }
        public int Deleted { get; set; }
        public int Unchanged { get; set; }
        public int ClosedVersions { get; set; }
        public int Tombstones { get; set; }
        public int SchemaChanges { get; set; }

        public JObject ToJson()
        {
            return new JObject
            {
                ["statements"] = Statements, ["inserted"] = Inserted, ["updated"] = Updated,
                ["deleted"] = Deleted, ["unchanged"] = Unchanged, ["closedVersions"] = ClosedVersions,
                ["tombstones"] = Tombstones, ["schemaChanges"] = SchemaChanges,
                ["totalChanged"] = Inserted + Updated + Deleted
            };
        }
    }

    private static class SqliteIntegrityValidator
    {
        public static JObject Validate(SqliteDatabase database, Action checkBudget)
        {
            JArray checks = new JArray();
            JArray errors = new JArray();
            HashSet<string> schemaNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SqliteSchemaEntry entry in database.SchemaEntries)
            {
                checkBudget();
                if (string.IsNullOrWhiteSpace(entry.Name)) errors.Add("A schema object has no name.");
                else if (!schemaNames.Add(entry.Type + ":" + entry.Name)) errors.Add("Duplicate schema object: " + entry.Type + ":" + entry.Name);
            }
            checks.Add("schema-names");

            foreach (SqliteTable table in database.Tables.Values)
            {
                HashSet<long> rowids = new HashSet<long>();
                for (int r = 0; r < table.Rows.Count; r++)
                {
                    if (!rowids.Add(table.Rows[r].RowId)) errors.Add("Duplicate rowid " + table.Rows[r].RowId + " in table " + table.Name + ".");
                    if (table.Rows[r].Values.Length != table.Columns.Count) errors.Add("Column-count mismatch in table " + table.Name + ".");
                    if (r > 0 && table.Rows[r - 1].RowId >= table.Rows[r].RowId) errors.Add("Rowids are not strictly ordered in table " + table.Name + ".");
                    for (int c = 0; c < table.Columns.Count; c++)
                        if (table.Columns[c].NotNull && !table.Columns[c].IntegerPrimaryKey && table.Rows[r].Values[c] == null)
                            errors.Add("NOT NULL violation in " + table.Name + "." + table.Columns[c].Name + ".");
                }
                ValidateUniqueIndexes(table, errors, checkBudget);
            }
            checks.Add("rowid-order-and-uniqueness");
            checks.Add("record-column-counts");
            checks.Add("not-null-constraints");
            checks.Add("supported-unique-indexes");
            ValidateForeignKeys(database, errors, checkBudget);
            checks.Add("foreign-key-references");

            return new JObject { ["ok"] = errors.Count == 0, ["checks"] = checks, ["errors"] = errors };
        }

        private static void ValidateForeignKeys(SqliteDatabase database, JArray errors, Action checkBudget)
        {
            foreach (SqliteTable child in database.Tables.Values)
            {
                if (child.ForeignKeys == null) continue;
                for (int f = 0; f < child.ForeignKeys.Count; f++)
                {
                    checkBudget();
                    SqliteForeignKeyDef foreignKey = child.ForeignKeys[f];
                    SqliteTable parent;
                    if (!database.Tables.TryGetValue(foreignKey.ParentTable, out parent))
                    {
                        errors.Add("Foreign key in " + child.Name + " references missing table " + foreignKey.ParentTable + ".");
                        continue;
                    }
                    List<string> parentColumns = foreignKey.ParentColumns == null
                        ? new List<string>()
                        : new List<string>(foreignKey.ParentColumns);
                    if (parentColumns.Count == 0)
                    {
                        if (parent.IntegerPrimaryKeyIndex >= 0) parentColumns.Add(parent.Columns[parent.IntegerPrimaryKeyIndex].Name);
                        else
                        {
                            SqliteUniqueKey primary = parent.UniqueKeys.FirstOrDefault(k => k.PrimaryKey);
                            if (primary != null) parentColumns.AddRange(primary.Columns);
                        }
                    }
                    if (parentColumns.Count == 0 || parentColumns.Count != foreignKey.ChildColumns.Count)
                    {
                        errors.Add("Foreign key in " + child.Name + " does not resolve to a compatible parent key in " + parent.Name + ".");
                        continue;
                    }

                    bool columnsValid = true;
                    for (int c = 0; c < parentColumns.Count; c++)
                        if (parent.FindColumn(parentColumns[c]) < 0 || child.FindColumn(foreignKey.ChildColumns[c]) < 0) columnsValid = false;
                    if (!columnsValid)
                    {
                        errors.Add("Foreign key in " + child.Name + " references an unknown child or parent column.");
                        continue;
                    }

                    HashSet<string> parentKeys = new HashSet<string>(StringComparer.Ordinal);
                    for (int r = 0; r < parent.Rows.Count; r++)
                    {
                        bool anyNull;
                        string key = BuildForeignKeyKey(parent, parent.Rows[r], parentColumns, parent, parentColumns, false, out anyNull);
                        if (!anyNull) parentKeys.Add(key);
                    }
                    for (int r = 0; r < child.Rows.Count; r++)
                    {
                        checkBudget();
                        bool anyNull;
                        string key = BuildForeignKeyKey(child, child.Rows[r], foreignKey.ChildColumns, parent, parentColumns, true, out anyNull);
                        if (!anyNull && !parentKeys.Contains(key))
                            errors.Add("Foreign key violation in " + child.Name + " rowid " + child.Rows[r].RowId + " referencing " + parent.Name + ".");
                    }
                }
            }
        }

        private static string BuildForeignKeyKey(SqliteTable source, SqliteRow row, IList<string> sourceColumns, SqliteTable parent, IList<string> parentColumns, bool applyParentAffinity, out bool anyNull)
        {
            anyNull = false;
            StringBuilder key = new StringBuilder();
            for (int i = 0; i < sourceColumns.Count; i++)
            {
                object value = row.Values[source.FindColumn(sourceColumns[i])];
                if (value == null) anyNull = true;
                SqliteColumnDef parentColumn = parent.Columns[parent.FindColumn(parentColumns[i])];
                if (applyParentAffinity) value = ApplyForeignKeyAffinity(parentColumn, value);
                if (i > 0) key.Append('\u001e');
                key.Append(ValueKey(value, parentColumn.Collation ?? "BINARY"));
            }
            return key.ToString();
        }

        private static object ApplyForeignKeyAffinity(SqliteColumnDef column, object value)
        {
            if (value == null) return null;
            if (value is DateTime) value = ((DateTime)value).ToString("o");
            string type = (column.DeclaredType ?? "").ToUpperInvariant();
            if (type.Contains("INT"))
            {
                long integer;
                if (value is bool) return (bool)value ? 1L : 0L;
                if (long.TryParse(value.ToString(), out integer)) return integer;
                return value;
            }
            if (type.Contains("CHAR") || type.Contains("CLOB") || type.Contains("TEXT")) return value is byte[] ? value : (object)value.ToString();
            if (type.Length == 0 || type.Contains("BLOB")) return value is bool ? ((bool)value ? 1L : 0L) : value;
            if (type.Contains("REAL") || type.Contains("FLOA") || type.Contains("DOUB"))
            {
                double real;
                if (double.TryParse(value.ToString(), out real)) return real;
                return value;
            }
            long whole;
            double number;
            if (long.TryParse(value.ToString(), out whole)) return whole;
            if (double.TryParse(value.ToString(), out number)) return number;
            return value is bool ? ((bool)value ? 1L : 0L) : value;
        }

        public static JObject ValidateRoundTrip(SqliteDatabase expected, SqliteDatabase actual, Action checkBudget)
        {
            JObject result = Validate(actual, checkBudget);
            JArray errors = (JArray)result["errors"];
            if (expected.Tables.Count != actual.Tables.Count) errors.Add("Table count changed during serialization.");
            foreach (KeyValuePair<string, SqliteTable> pair in expected.Tables)
            {
                checkBudget();
                SqliteTable roundTrip;
                if (!actual.Tables.TryGetValue(pair.Key, out roundTrip)) { errors.Add("Missing table after serialization: " + pair.Key); continue; }
                SqliteTable source = pair.Value;
                if (source.Columns.Count != roundTrip.Columns.Count) { errors.Add("Column count changed for table " + pair.Key + "."); continue; }
                for (int c = 0; c < source.Columns.Count; c++)
                    if (!string.Equals(source.Columns[c].Name, roundTrip.Columns[c].Name, StringComparison.OrdinalIgnoreCase))
                        errors.Add("Column name changed for table " + pair.Key + " at ordinal " + c + ".");
                if (source.Rows.Count != roundTrip.Rows.Count) { errors.Add("Row count changed for table " + pair.Key + "."); continue; }
                for (int r = 0; r < source.Rows.Count; r++)
                {
                    if (source.Rows[r].RowId != roundTrip.Rows[r].RowId) { errors.Add("Rowid changed for table " + pair.Key + " at row " + r + "."); break; }
                    for (int c = 0; c < source.Columns.Count; c++)
                        if (!ValuesEqual(source.Rows[r].Values[c], roundTrip.Rows[r].Values[c]))
                        {
                            errors.Add("Value changed for table " + pair.Key + " at row " + r + ", column " + source.Columns[c].Name + ".");
                            r = source.Rows.Count;
                            break;
                        }
                }
            }
            HashSet<string> expectedSchema = new HashSet<string>(expected.SchemaEntries.Select(e => SchemaKey(e)), StringComparer.Ordinal);
            HashSet<string> actualSchema = new HashSet<string>(actual.SchemaEntries.Select(e => SchemaKey(e)), StringComparer.Ordinal);
            if (!expectedSchema.SetEquals(actualSchema)) errors.Add("Logical sqlite_schema entries changed during serialization.");
            ((JArray)result["checks"]).Add("serialize-reopen-logical-roundtrip");
            result["ok"] = errors.Count == 0;
            result["sourceTableCount"] = expected.Tables.Count;
            result["reopenedTableCount"] = actual.Tables.Count;
            return result;
        }

        private static string SchemaKey(SqliteSchemaEntry entry)
        {
            return (entry.Type ?? "") + "\u001f" + (entry.Name ?? "") + "\u001f" + (entry.TableName ?? "") + "\u001f" + (entry.Sql ?? "");
        }

        private static void ValidateUniqueIndexes(SqliteTable table, JArray errors, Action checkBudget)
        {
            foreach (SqliteIndexDef index in table.Indexes)
            {
                if (!index.Unique || !index.Supported) continue;
                Dictionary<string, long> seen = new Dictionary<string, long>(StringComparer.Ordinal);
                for (int r = 0; r < table.Rows.Count; r++)
                {
                    checkBudget();
                    bool anyNull;
                    string key = BuildKey(table, table.Rows[r], index.Columns, index.Collations, out anyNull);
                    if (anyNull) continue;
                    long existing;
                    if (seen.TryGetValue(key, out existing)) errors.Add("Unique index violation in " + index.Schema.Name + " for rowids " + existing + " and " + table.Rows[r].RowId + ".");
                    else seen[key] = table.Rows[r].RowId;
                }
            }
        }

        public static string BuildKey(SqliteTable table, SqliteRow row, IList<string> columns, IList<string> collations, out bool anyNull)
        {
            anyNull = false;
            StringBuilder key = new StringBuilder();
            for (int i = 0; i < columns.Count; i++)
            {
                int column = table.FindColumn(columns[i]);
                if (column < 0) throw new SqliteConnectorException(HttpStatusCode.BadRequest, "UNKNOWN_COLUMN", "Unknown column '" + columns[i] + "' in table '" + table.Name + "'.", null);
                object value = row.Values[column];
                if (value == null) anyNull = true;
                if (i > 0) key.Append('\u001e');
                key.Append(ValueKey(value, collations == null || i >= collations.Count ? "BINARY" : collations[i]));
            }
            return key.ToString();
        }

        public static string BuildKey(RowSet source, object[] row, IList<string> columns)
        {
            StringBuilder key = new StringBuilder();
            for (int i = 0; i < columns.Count; i++)
            {
                int column = -1;
                for (int c = 0; c < source.Columns.Count; c++)
                    if (string.Equals(source.Columns[c].Name, columns[i], StringComparison.OrdinalIgnoreCase)) { column = c; break; }
                if (column < 0) throw new SqliteConnectorException(HttpStatusCode.BadRequest, "UNKNOWN_SOURCE_COLUMN", "The input does not contain key column '" + columns[i] + "'.", null);
                if (i > 0) key.Append('\u001e');
                key.Append(ValueKey(row[column], "BINARY"));
            }
            return key.ToString();
        }

        private static string ValueKey(object value, string collation)
        {
            if (value == null) return "N";
            byte[] blob = value as byte[];
            if (blob != null) return "B" + Convert.ToBase64String(blob);
            if (value is bool) return "N:I:" + ((bool)value ? "1" : "0");
            if (value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint || value is long || value is ulong)
                return "N:I:" + value.ToString();
            if (value is float || value is double || value is decimal)
            {
                double number = Convert.ToDouble(value);
                if (!double.IsNaN(number) && !double.IsInfinity(number) && number >= long.MinValue && number <= long.MaxValue && Math.Truncate(number) == number)
                    return "N:I:" + Convert.ToInt64(number);
                return "N:R:" + number.ToString("R");
            }
            string text = value is DateTime ? ((DateTime)value).ToString("o") : value.ToString();
            if (string.Equals(collation, "NOCASE", StringComparison.OrdinalIgnoreCase)) text = FoldAsciiNoCase(text);
            else if (string.Equals(collation, "RTRIM", StringComparison.OrdinalIgnoreCase)) text = text.TrimEnd(' ');
            return "T" + text.Length + ":" + text;
        }

        private static string FoldAsciiNoCase(string value)
        {
            char[] chars = value.ToCharArray();
            for (int i = 0; i < chars.Length; i++) if (chars[i] >= 'A' && chars[i] <= 'Z') chars[i] = (char)(chars[i] + 32);
            return new string(chars);
        }

        public static bool ValuesEqual(object left, object right)
        {
            if (left == null || right == null) return left == null && right == null;
            byte[] lb = left as byte[], rb = right as byte[];
            if (lb != null || rb != null) return lb != null && rb != null && lb.SequenceEqual(rb);
            if (IsIntegral(left) && IsIntegral(right))
            {
                return Convert.ToInt64(left) == Convert.ToInt64(right);
            }
            if (IsNumeric(left) && IsNumeric(right))
                return Convert.ToDouble(left).Equals(Convert.ToDouble(right));
            string ls = left is DateTime ? ((DateTime)left).ToString("o") : left.ToString();
            string rs = right is DateTime ? ((DateTime)right).ToString("o") : right.ToString();
            return string.Equals(ls, rs, StringComparison.Ordinal);
        }

        private static bool IsIntegral(object value)
        {
            return value is bool || value is byte || value is sbyte || value is short || value is ushort
                || value is int || value is uint || value is long;
        }

        private static bool IsNumeric(object value)
        {
            return IsIntegral(value) || value is float || value is double || value is decimal;
        }
    }


    // ==================== SQL DML, bulk upsert, and SCD2 ====================

    private sealed class SqliteMutationEngine
    {
        private readonly Script _owner;
        private readonly SqliteDatabase _database;
        private readonly QueryExecutionOptions _options;
        private readonly Action _checkBudget;
        private readonly Dictionary<string, long> _lastAllocatedRowIds;

        public SqliteMutationEngine(Script owner, SqliteDatabase database, QueryExecutionOptions options, Action checkBudget)
        {
            _owner = owner;
            _database = database;
            _options = options;
            _checkBudget = checkBudget;
            _lastAllocatedRowIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            _database.EnsureWritable();
        }

        public SqliteChangeSummary ExecuteStatements(string sql)
        {
            List<string> statements = SplitStatements(sql);
            if (statements.Count == 0) throw new DatasetException("INVALID_REQUEST", "The SQL text contains no statements.", null);
            if (statements.Count > _database.Limits.MaxSqlStatements)
                throw new DatasetException("STATEMENT_LIMIT_EXCEEDED", "The SQL text exceeds maxSqlStatements.", new JObject { ["statementCount"] = statements.Count, ["maxSqlStatements"] = _database.Limits.MaxSqlStatements });

            SqliteChangeSummary summary = new SqliteChangeSummary();
            for (int i = 0; i < statements.Count; i++)
            {
                _checkBudget();
                string statement = statements[i].Trim();
                if (StartsWithWord(statement, "INSERT")) ApplyInsert(statement, summary);
                else if (StartsWithWord(statement, "UPDATE")) ApplyUpdate(statement, summary);
                else if (StartsWithWord(statement, "DELETE")) ApplyDelete(statement, summary);
                else if (StartsWithWord(statement, "PRAGMA")) ApplyPragma(statement, summary);
                else if (StartsWithWord(statement, "BEGIN") || StartsWithWord(statement, "COMMIT") || StartsWithWord(statement, "END")) { }
                else throw new DatasetException("UNSUPPORTED_SQL_STATEMENT", "ExecuteSqlite supports INSERT, UPDATE, DELETE, PRAGMA user_version/application_id, and transaction wrappers. Unsupported statement: " + FirstWord(statement) + ".", new JObject { ["statementIndex"] = i });
                summary.Statements++;
            }
            JObject integrity = SqliteIntegrityValidator.Validate(_database, _checkBudget);
            if (!(integrity["ok"]?.ToObject<bool>() ?? false))
                throw new SqliteConnectorException(HttpStatusCode.BadRequest, "CONSTRAINT_VALIDATION_FAILED", "The SQL batch would leave invalid table constraints.", integrity);
            return summary;
        }

        public SqliteChangeSummary BulkUpsert(JObject body)
        {
            string tableName = GetRequiredString(body, "table", "The 'table' field is required.");
            SqliteTable table = GetTable(tableName);
            RowSet source = ReadInputRows(body);
            List<string> keys = ReadStringArray(body["keyColumns"], "keyColumns", true);
            List<string> updates = ReadStringArray(body["updateColumns"], "updateColumns", false);
            string mode = body["mode"]?.ToString() ?? "Upsert";
            if (!string.Equals(mode, "Upsert", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(mode, "InsertOnly", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(mode, "UpdateOnly", StringComparison.OrdinalIgnoreCase))
                throw new DatasetException("INVALID_REQUEST", "mode must be Upsert, InsertOnly, or UpdateOnly.", null);
            string nullBehavior = body["nullUpdateBehavior"]?.ToString() ?? "OverwriteNulls";
            if (!string.Equals(nullBehavior, "OverwriteNulls", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(nullBehavior, "IgnoreNulls", StringComparison.OrdinalIgnoreCase))
                throw new DatasetException("INVALID_REQUEST", "nullUpdateBehavior must be OverwriteNulls or IgnoreNulls.", null);
            bool ignoreNulls = string.Equals(nullBehavior, "IgnoreNulls", StringComparison.OrdinalIgnoreCase);

            if (updates.Count == 0)
            {
                HashSet<string> keySet = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
                updates = source.Columns.Select(c => c.Name).Where(n => !keySet.Contains(n)).ToList();
            }
            ValidateColumnList(table, keys);
            ValidateColumnList(table, updates);

            Dictionary<string, SqliteRow> existing = new Dictionary<string, SqliteRow>(StringComparer.Ordinal);
            List<string> keyCollations = keys.Select(k => table.Columns[table.FindColumn(k)].Collation ?? "BINARY").ToList();
            for (int i = 0; i < table.Rows.Count; i++)
            {
                bool anyNull;
                string key = SqliteIntegrityValidator.BuildKey(table, table.Rows[i], keys, keyCollations, out anyNull);
                if (anyNull) throw new DatasetException("NULL_BUSINESS_KEY", "Existing rowid " + table.Rows[i].RowId + " has a null bulk key.", null);
                if (existing.ContainsKey(key)) throw new DatasetException("DUPLICATE_EXISTING_KEY", "The target table contains duplicate rows for the supplied key columns.", new JObject { ["table"] = table.Name, ["key"] = key });
                existing[key] = table.Rows[i];
            }

            SqliteChangeSummary summary = new SqliteChangeSummary { Statements = 1 };
            HashSet<string> sourceKeys = new HashSet<string>(StringComparer.Ordinal);
            for (int r = 0; r < source.Rows.Count; r++)
            {
                _checkBudget();
                object[] mapped = MapSourceRow(source, source.Rows[r], table, null);
                SqliteRow candidate = new SqliteRow { RowId = 0, Values = mapped };
                bool anyNull;
                string key = SqliteIntegrityValidator.BuildKey(table, candidate, keys, keyCollations, out anyNull);
                if (anyNull) throw new DatasetException("NULL_BUSINESS_KEY", "Input row " + r + " has a null bulk key.", null);
                if (!sourceKeys.Add(key)) throw new DatasetException("DUPLICATE_SOURCE_KEY", "The input contains duplicate rows for the supplied key columns.", new JObject { ["sourceRow"] = r, ["key"] = key });

                SqliteRow target;
                if (existing.TryGetValue(key, out target))
                {
                    if (string.Equals(mode, "InsertOnly", StringComparison.OrdinalIgnoreCase))
                        throw new DatasetException("INSERT_CONFLICT", "InsertOnly encountered an existing key.", new JObject { ["sourceRow"] = r, ["key"] = key });
                    bool changed = false;
                    for (int u = 0; u < updates.Count; u++)
                    {
                        int tc = table.FindColumn(updates[u]);
                        int sc = FindSourceColumn(source, updates[u]);
                        if (sc < 0) continue;
                        object value = ApplyAffinity(table.Columns[tc], source.Rows[r][sc]);
                        if (ignoreNulls && value == null) continue;
                        if (!SqliteIntegrityValidator.ValuesEqual(target.Values[tc], value)) { target.Values[tc] = value; changed = true; }
                    }
                    if (changed) summary.Updated++; else summary.Unchanged++;
                }
                else
                {
                    if (string.Equals(mode, "UpdateOnly", StringComparison.OrdinalIgnoreCase)) { summary.Unchanged++; continue; }
                    SqliteRow inserted = CreateInsertedRow(table, mapped, "ABORT", true);
                    table.Rows.Add(inserted);
                    existing[key] = inserted;
                    summary.Inserted++;
                }
            }
            table.Rows.Sort((a, b) => a.RowId.CompareTo(b.RowId));
            EnsureConstraints(table);
            return summary;
        }

        private void ApplyInsert(string statement, SqliteChangeSummary summary)
        {
            int position = 0;
            ExpectWord(statement, ref position, "INSERT");
            string resolution = "ABORT";
            if (TryConsumeWord(statement, ref position, "OR"))
            {
                resolution = ReadWord(statement, ref position).ToUpperInvariant();
                if (resolution != "ABORT" && resolution != "IGNORE" && resolution != "REPLACE")
                    throw new DatasetException("UNSUPPORTED_CONFLICT_ACTION", "INSERT OR supports ABORT, IGNORE, or REPLACE.", null);
            }
            ExpectWord(statement, ref position, "INTO");
            string tableName = SqliteDdl.ReadIdentifier(statement, ref position);
            SqliteTable table = GetTable(tableName);
            List<string> columns = new List<string>();
            SkipWhitespace(statement, ref position);
            if (position < statement.Length && statement[position] == '(')
            {
                int close = SqliteDdl.FindMatchingParen(statement, position);
                if (close < 0) throw new DatasetException("SQL_PARSE_ERROR", "INSERT column list is not closed.", null);
                foreach (string part in SqliteDdl.SplitTopLevel(statement.Substring(position + 1, close - position - 1), ','))
                {
                    int p = 0;
                    string column = SqliteDdl.ReadIdentifier(part, ref p);
                    if (column == null) throw new DatasetException("SQL_PARSE_ERROR", "An INSERT column name is invalid.", null);
                    columns.Add(column);
                }
                position = close + 1;
            }
            else columns.AddRange(table.Columns.Select(c => c.Name));
            ValidateColumnList(table, columns);
            string tail = statement.Substring(position).Trim();
            if (SqliteDdl.IndexOfWord(tail, "ON", 0) >= 0 && SqliteDdl.IndexOfWord(tail, "CONFLICT", 0) >= 0)
                throw new DatasetException("UNSUPPORTED_SQL_FEATURE", "SQL ON CONFLICT clauses are not supported by ExecuteSqlite; use BulkUpsertSqlite for linear-time upserts.", null);

            List<object[]> sourceRows = new List<object[]>();
            if (StartsWithWord(tail, "VALUES"))
            {
                int p = 6;
                while (p < tail.Length)
                {
                    SkipWhitespaceAndComma(tail, ref p);
                    if (p >= tail.Length) break;
                    if (tail[p] != '(') throw new DatasetException("SQL_PARSE_ERROR", "Expected '(' in INSERT VALUES.", null);
                    int close = SqliteDdl.FindMatchingParen(tail, p);
                    if (close < 0) throw new DatasetException("SQL_PARSE_ERROR", "An INSERT VALUES tuple is not closed.", null);
                    List<string> expressions = SqliteDdl.SplitTopLevel(tail.Substring(p + 1, close - p - 1), ',');
                    if (expressions.Count != columns.Count) throw new DatasetException("INSERT_COLUMN_COUNT_MISMATCH", "INSERT VALUES count does not match the target column list.", null);
                    object[] values = new object[expressions.Count];
                    for (int i = 0; i < expressions.Count; i++)
                        values[i] = string.Equals(expressions[i].Trim(), "DEFAULT", StringComparison.OrdinalIgnoreCase) ? DefaultMarker.Value : EvaluateScalar(expressions[i]);
                    sourceRows.Add(values);
                    p = close + 1;
                }
            }
            else if (StartsWithWord(tail, "SELECT") || StartsWithWord(tail, "WITH"))
            {
                RowSet selected = ExecuteSelect(tail, null, false);
                if (selected.Columns.Count != columns.Count) throw new DatasetException("INSERT_COLUMN_COUNT_MISMATCH", "INSERT SELECT count does not match the target column list.", null);
                sourceRows.AddRange(selected.Rows);
            }
            else throw new DatasetException("UNSUPPORTED_SQL_FEATURE", "INSERT requires VALUES or SELECT.", null);

            for (int r = 0; r < sourceRows.Count; r++)
            {
                object[] targetValues = BuildDefaultRow(table);
                for (int c = 0; c < columns.Count; c++)
                {
                    int tc = table.FindColumn(columns[c]);
                    if (!ReferenceEquals(sourceRows[r][c], DefaultMarker.Value)) targetValues[tc] = ApplyAffinity(table.Columns[tc], sourceRows[r][c]);
                }
                SqliteRow inserted = CreateInsertedRow(table, targetValues, resolution);
                if (inserted == null) { summary.Unchanged++; continue; }
                table.Rows.Add(inserted);
                summary.Inserted++;
            }
            table.Rows.Sort((a, b) => a.RowId.CompareTo(b.RowId));
            EnsureConstraints(table);
        }

        private void ApplyUpdate(string statement, SqliteChangeSummary summary)
        {
            int position = 0;
            ExpectWord(statement, ref position, "UPDATE");
            if (TryConsumeWord(statement, ref position, "OR"))
                throw new DatasetException("UNSUPPORTED_SQL_FEATURE", "UPDATE OR conflict actions are not supported.", null);
            string tableName = SqliteDdl.ReadIdentifier(statement, ref position);
            SqliteTable table = GetTable(tableName);
            ExpectWord(statement, ref position, "SET");
            int where = SqliteDdl.IndexOfWord(statement, "WHERE", position);
            string assignmentsText = where < 0 ? statement.Substring(position) : statement.Substring(position, where - position);
            string whereText = where < 0 ? null : statement.Substring(where + 5).Trim();
            List<string> assignments = SqliteDdl.SplitTopLevel(assignmentsText, ',');
            List<string> columns = new List<string>();
            List<string> expressions = new List<string>();
            for (int i = 0; i < assignments.Count; i++)
            {
                int equal = FindTopLevelEquals(assignments[i]);
                if (equal < 0) throw new DatasetException("SQL_PARSE_ERROR", "UPDATE assignment is missing '='.", null);
                int p = 0;
                string column = SqliteDdl.ReadIdentifier(assignments[i].Substring(0, equal), ref p);
                if (column == null || table.FindColumn(column) < 0) throw new DatasetException("UNKNOWN_COLUMN", "Unknown UPDATE column '" + column + "'.", null);
                columns.Add(column);
                expressions.Add(assignments[i].Substring(equal + 1).Trim());
            }

            StringBuilder select = new StringBuilder("SELECT __pa_rowid");
            for (int i = 0; i < expressions.Count; i++) select.Append(", ").Append(expressions[i]).Append(" AS \"__pa_v").Append(i).Append("\"");
            select.Append(" FROM ").Append(QuoteIdentifier(table.Name));
            if (!string.IsNullOrWhiteSpace(whereText)) select.Append(" WHERE ").Append(whereText);
            RowSet evaluated = ExecuteSelect(select.ToString(), table, true);
            Dictionary<long, SqliteRow> rows = table.Rows.ToDictionary(r => r.RowId);
            for (int r = 0; r < evaluated.Rows.Count; r++)
            {
                long rowid = Convert.ToInt64(evaluated.Rows[r][0]);
                SqliteRow target = rows[rowid];
                for (int c = 0; c < columns.Count; c++)
                {
                    int tc = table.FindColumn(columns[c]);
                    object value = ApplyAffinity(table.Columns[tc], evaluated.Rows[r][c + 1]);
                    if (table.Columns[tc].IntegerPrimaryKey)
                    {
                        if (value == null) throw new DatasetException("INVALID_ROWID", "An INTEGER PRIMARY KEY cannot be set to null by UPDATE.", null);
                        target.RowId = Convert.ToInt64(value);
                        target.Values[tc] = target.RowId;
                    }
                    else target.Values[tc] = value;
                }
                summary.Updated++;
            }
            table.Rows.Sort((a, b) => a.RowId.CompareTo(b.RowId));
            EnsureConstraints(table);
        }

        private void ApplyDelete(string statement, SqliteChangeSummary summary)
        {
            int position = 0;
            ExpectWord(statement, ref position, "DELETE");
            ExpectWord(statement, ref position, "FROM");
            string tableName = SqliteDdl.ReadIdentifier(statement, ref position);
            SqliteTable table = GetTable(tableName);
            int where = SqliteDdl.IndexOfWord(statement, "WHERE", position);
            string whereText = where < 0 ? null : statement.Substring(where + 5).Trim();
            string select = "SELECT __pa_rowid FROM " + QuoteIdentifier(table.Name) + (string.IsNullOrWhiteSpace(whereText) ? "" : " WHERE " + whereText);
            RowSet selected = ExecuteSelect(select, table, true);
            HashSet<long> rowids = new HashSet<long>(selected.Rows.Select(r => Convert.ToInt64(r[0])));
            int before = table.Rows.Count;
            table.Rows.RemoveAll(r => rowids.Contains(r.RowId));
            summary.Deleted += before - table.Rows.Count;
        }

        private void ApplyPragma(string statement, SqliteChangeSummary summary)
        {
            Match match = Regex.Match(statement, @"^\s*PRAGMA\s+(user_version|application_id)\s*=\s*([0-9]+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) throw new DatasetException("UNSUPPORTED_PRAGMA", "Only PRAGMA user_version = N and PRAGMA application_id = N are writable.", null);
            uint value;
            if (!uint.TryParse(match.Groups[2].Value, out value)) throw new DatasetException("INVALID_PRAGMA_VALUE", "PRAGMA value must fit an unsigned 32-bit integer.", null);
            if (string.Equals(match.Groups[1].Value, "user_version", StringComparison.OrdinalIgnoreCase)) _database.UserVersion = value;
            else _database.ApplicationId = value;
            summary.Updated++;
        }

        private RowSet ExecuteSelect(string sql, SqliteTable target, bool includeInternalRowId)
        {
            Dictionary<string, RowSet> sources = _database.ToRowSets(_options);
            if (target != null) sources[target.Name] = target.ToRowSet(includeInternalRowId);
            return new QueryExecutor(_options).ExecuteQuery(new SqlParser(sql).ParseQuery(), sources);
        }

        private object EvaluateScalar(string expression)
        {
            RowSet result = ExecuteSelect("SELECT " + expression + " AS value", null, false);
            if (result.Rows.Count != 1 || result.Columns.Count != 1) throw new DatasetException("SQL_EXPRESSION_ERROR", "Expression did not produce one scalar value.", null);
            return result.Rows[0][0];
        }

        private SqliteTable GetTable(string name)
        {
            SqliteTable table;
            if (string.IsNullOrWhiteSpace(name) || !_database.Tables.TryGetValue(name, out table))
                throw new DatasetException("UNKNOWN_TABLE", "Unknown SQLite table: " + name + ".", null);
            if (!table.Writable) throw new DatasetException("UNSUPPORTED_SCHEMA", "Table '" + table.Name + "' is not writable: " + table.UnsupportedReason, null);
            return table;
        }

        private RowSet ReadInputRows(JObject body)
        {
            string format = body["format"]?.ToString();
            if (string.IsNullOrWhiteSpace(format)) format = body["jsonRows"] is JArray ? "Json" : "Csv";
            JObject typeOverrides = body["typeOverrides"] as JObject;
            if (string.Equals(format, "Json", StringComparison.OrdinalIgnoreCase))
                return _owner.BuildRowSetFromJson("__source", body["jsonRows"] as JArray, typeOverrides, false, _options);
            if (string.Equals(format, "Csv", StringComparison.OrdinalIgnoreCase))
                return _owner.BuildRowSetFromCsv("__source", body["csvText"]?.ToString(), ParseCsvOptions(body["csvOptions"] as JObject), typeOverrides, _options);
            throw new DatasetException("INVALID_INPUT_FORMAT", "The input format must be Json or Csv.", null);
        }

        private object[] MapSourceRow(RowSet source, object[] sourceRow, SqliteTable table, object[] baseValues)
        {
            object[] values = baseValues == null ? BuildDefaultRow(table) : (object[])baseValues.Clone();
            for (int c = 0; c < source.Columns.Count; c++)
            {
                int target = table.FindColumn(source.Columns[c].Name);
                if (target >= 0) values[target] = ApplyAffinity(table.Columns[target], sourceRow[c]);
            }
            return values;
        }

        private static int FindSourceColumn(RowSet source, string name)
        {
            for (int i = 0; i < source.Columns.Count; i++) if (string.Equals(source.Columns[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private object[] BuildDefaultRow(SqliteTable table)
        {
            object[] values = new object[table.Columns.Count];
            for (int i = 0; i < table.Columns.Count; i++)
            {
                string defaultSql = table.Columns[i].DefaultSql;
                if (!string.IsNullOrWhiteSpace(defaultSql))
                {
                    string expression = defaultSql.Trim();
                    if (expression.StartsWith("(") && expression.EndsWith(")")) expression = expression.Substring(1, expression.Length - 2);
                    if (string.Equals(expression, "CURRENT_TIMESTAMP", StringComparison.OrdinalIgnoreCase)) values[i] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
                    else if (string.Equals(expression, "CURRENT_DATE", StringComparison.OrdinalIgnoreCase)) values[i] = DateTime.UtcNow.ToString("yyyy-MM-dd");
                    else if (string.Equals(expression, "CURRENT_TIME", StringComparison.OrdinalIgnoreCase)) values[i] = DateTime.UtcNow.ToString("HH:mm:ss");
                    else
                    {
                        try { values[i] = ApplyAffinity(table.Columns[i], EvaluateScalar(expression)); }
                        catch (Exception ex) { throw new DatasetException("UNSUPPORTED_DEFAULT", "Default expression for '" + table.Name + "." + table.Columns[i].Name + "' is not supported: " + ex.Message, null); }
                    }
                }
            }
            return values;
        }

        private SqliteRow CreateInsertedRow(SqliteTable table, object[] values, string resolution, bool deferConstraintCheck = false)
        {
            long rowid;
            if (table.IntegerPrimaryKeyIndex >= 0 && values[table.IntegerPrimaryKeyIndex] != null)
            {
                rowid = Convert.ToInt64(values[table.IntegerPrimaryKeyIndex]);
                AdvanceRowIdAllocator(table, rowid);
            }
            else
            {
                rowid = AllocateRowId(table);
                if (table.IntegerPrimaryKeyIndex >= 0) values[table.IntegerPrimaryKeyIndex] = rowid;
            }
            SqliteRow candidate = new SqliteRow { RowId = rowid, Values = values };
            if (!deferConstraintCheck)
            {
                List<SqliteRow> conflicts = FindConflicts(table, candidate);
                if (conflicts.Count > 0)
                {
                    if (resolution == "IGNORE") return null;
                    if (resolution == "REPLACE") table.Rows.RemoveAll(r => conflicts.Contains(r));
                    else throw new DatasetException("INSERT_CONFLICT", "INSERT violates a rowid, PRIMARY KEY, or UNIQUE constraint.", new JObject { ["table"] = table.Name, ["rowid"] = rowid });
                }
            }
            ValidateNotNull(table, candidate);
            UpdateAutoincrementSequence(table, rowid);
            return candidate;
        }

        private long AllocateRowId(SqliteTable table)
        {
            long last;
            if (!_lastAllocatedRowIds.TryGetValue(table.Name, out last))
            {
                last = table.Rows.Count == 0 ? 0L : Math.Max(0L, table.Rows.Max(r => r.RowId));
                if (IsAutoincrement(table)) last = Math.Max(last, ReadAutoincrementSequence(table.Name));
            }
            if (last == long.MaxValue)
                throw new SqliteConnectorException(HttpStatusCode.BadRequest, "ROWID_EXHAUSTED", "The table '" + table.Name + "' has exhausted supported positive rowids.", null);
            long next = last + 1L;
            _lastAllocatedRowIds[table.Name] = next;
            return next;
        }

        private void AdvanceRowIdAllocator(SqliteTable table, long rowid)
        {
            long last;
            if (!_lastAllocatedRowIds.TryGetValue(table.Name, out last))
                last = table.Rows.Count == 0 ? 0L : Math.Max(0L, table.Rows.Max(r => r.RowId));
            if (rowid > last) _lastAllocatedRowIds[table.Name] = rowid;
        }

        private static bool IsAutoincrement(SqliteTable table)
        {
            return table.IntegerPrimaryKeyIndex >= 0 && table.Columns[table.IntegerPrimaryKeyIndex].AutoIncrement;
        }

        private long ReadAutoincrementSequence(string tableName)
        {
            SqliteTable sequence;
            if (!_database.Tables.TryGetValue("sqlite_sequence", out sequence)) return 0L;
            int nameColumn = sequence.FindColumn("name"), valueColumn = sequence.FindColumn("seq");
            if (nameColumn < 0 || valueColumn < 0) return 0L;
            for (int i = 0; i < sequence.Rows.Count; i++)
            {
                object name = sequence.Rows[i].Values[nameColumn];
                if (name == null || !string.Equals(name.ToString(), tableName, StringComparison.OrdinalIgnoreCase)) continue;
                long value;
                return sequence.Rows[i].Values[valueColumn] != null && long.TryParse(sequence.Rows[i].Values[valueColumn].ToString(), out value) ? value : 0L;
            }
            return 0L;
        }

        private void UpdateAutoincrementSequence(SqliteTable table, long rowid)
        {
            if (!IsAutoincrement(table)) return;
            SqliteTable sequence;
            if (!_database.Tables.TryGetValue("sqlite_sequence", out sequence))
                throw new SqliteConnectorException(HttpStatusCode.BadRequest, "INVALID_AUTOINCREMENT_SCHEMA", "AUTOINCREMENT table '" + table.Name + "' has no sqlite_sequence table.", null);
            int nameColumn = sequence.FindColumn("name"), valueColumn = sequence.FindColumn("seq");
            if (nameColumn < 0 || valueColumn < 0)
                throw new SqliteConnectorException(HttpStatusCode.BadRequest, "INVALID_AUTOINCREMENT_SCHEMA", "The sqlite_sequence table is malformed.", null);
            for (int i = 0; i < sequence.Rows.Count; i++)
            {
                object name = sequence.Rows[i].Values[nameColumn];
                if (name == null || !string.Equals(name.ToString(), table.Name, StringComparison.OrdinalIgnoreCase)) continue;
                long current;
                if (sequence.Rows[i].Values[valueColumn] == null || !long.TryParse(sequence.Rows[i].Values[valueColumn].ToString(), out current) || rowid > current)
                    sequence.Rows[i].Values[valueColumn] = rowid;
                return;
            }
            object[] values = new object[sequence.Columns.Count];
            values[nameColumn] = table.Name;
            values[valueColumn] = rowid;
            long sequenceRowId = AllocateRowId(sequence);
            sequence.Rows.Add(new SqliteRow { RowId = sequenceRowId, Values = values });
            sequence.Rows.Sort((a, b) => a.RowId.CompareTo(b.RowId));
        }

        private List<SqliteRow> FindConflicts(SqliteTable table, SqliteRow candidate)
        {
            List<SqliteRow> conflicts = table.Rows.Where(r => r.RowId == candidate.RowId).ToList();
            foreach (SqliteIndexDef index in table.Indexes)
            {
                if (!index.Unique || !index.Supported) continue;
                bool nullCandidate;
                string key = SqliteIntegrityValidator.BuildKey(table, candidate, index.Columns, index.Collations, out nullCandidate);
                if (nullCandidate) continue;
                for (int r = 0; r < table.Rows.Count; r++)
                {
                    bool nullExisting;
                    string existing = SqliteIntegrityValidator.BuildKey(table, table.Rows[r], index.Columns, index.Collations, out nullExisting);
                    if (!nullExisting && string.Equals(key, existing, StringComparison.Ordinal) && !conflicts.Contains(table.Rows[r])) conflicts.Add(table.Rows[r]);
                }
            }
            return conflicts;
        }

        private static object ApplyAffinity(SqliteColumnDef column, object value)
        {
            if (value == null) return null;
            if (value is DateTime) value = ((DateTime)value).ToString("o");
            string type = (column.DeclaredType ?? "").ToUpperInvariant();
            if (type.Contains("INT"))
            {
                long l;
                if (value is bool) return (bool)value ? 1L : 0L;
                if (long.TryParse(value.ToString(), out l)) return l;
            }
            if (type.Contains("CHAR") || type.Contains("CLOB") || type.Contains("TEXT")) return value is byte[] ? value : (object)value.ToString();
            if (type.Length == 0 || type.Contains("BLOB")) return value is bool ? ((bool)value ? 1L : 0L) : value;
            if (type.Contains("REAL") || type.Contains("FLOA") || type.Contains("DOUB"))
            {
                double d;
                if (double.TryParse(value.ToString(), out d)) return d;
            }
            if (!type.Contains("BLOB"))
            {
                long l;
                double d;
                if (long.TryParse(value.ToString(), out l)) return l;
                if (double.TryParse(value.ToString(), out d)) return d;
            }
            if (value is bool) return (bool)value ? 1L : 0L;
            return value;
        }

        private void EnsureConstraints(SqliteTable table)
        {
            JArray errors = new JArray();
            HashSet<long> rowids = new HashSet<long>();
            for (int i = 0; i < table.Rows.Count; i++)
            {
                if (!rowids.Add(table.Rows[i].RowId)) errors.Add("Duplicate rowid " + table.Rows[i].RowId + ".");
                ValidateNotNull(table, table.Rows[i], errors);
            }
            JObject all = SqliteIntegrityValidator.Validate(_database, _checkBudget);
            foreach (JToken error in (JArray)all["errors"]) errors.Add(error);
            if (errors.Count > 0) throw new SqliteConnectorException(HttpStatusCode.BadRequest, "CONSTRAINT_VALIDATION_FAILED", "The mutation violates supported SQLite constraints.", new JObject { ["errors"] = errors });
        }

        private static void ValidateNotNull(SqliteTable table, SqliteRow row)
        {
            JArray errors = new JArray();
            ValidateNotNull(table, row, errors);
            if (errors.Count > 0) throw new DatasetException("NOT_NULL_VIOLATION", errors[0].ToString(), null);
        }

        private static void ValidateNotNull(SqliteTable table, SqliteRow row, JArray errors)
        {
            for (int c = 0; c < table.Columns.Count; c++)
                if (table.Columns[c].NotNull && !table.Columns[c].IntegerPrimaryKey && row.Values[c] == null)
                    errors.Add("NOT NULL violation in " + table.Name + "." + table.Columns[c].Name + ".");
        }

        private static void ValidateColumnList(SqliteTable table, IEnumerable<string> columns)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string column in columns)
            {
                if (table.FindColumn(column) < 0) throw new DatasetException("UNKNOWN_COLUMN", "Unknown column '" + column + "' in table '" + table.Name + "'.", null);
                if (!seen.Add(column)) throw new DatasetException("DUPLICATE_COLUMN", "Column '" + column + "' is repeated.", null);
            }
        }

        private static List<string> ReadStringArray(JToken token, string name, bool required)
        {
            JArray array = token as JArray;
            if (array == null)
            {
                if (required) throw new DatasetException("INVALID_REQUEST", "The '" + name + "' field must be a non-empty string array.", null);
                return new List<string>();
            }
            List<string> values = array.Select(v => v?.ToString()).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            if (required && values.Count == 0) throw new DatasetException("INVALID_REQUEST", "The '" + name + "' field must be a non-empty string array.", null);
            return values;
        }

        private sealed class DefaultMarker { public static readonly object Value = new object(); }


        public SqliteChangeSummary ApplyScd2(JObject body)
        {
            string tableName = GetRequiredString(body, "table", "The 'table' field is required.");
            SqliteTable table = GetTable(tableName);
            RowSet source = ReadInputRows(body);
            List<string> keys = ReadStringArray(body["businessKeyColumns"], "businessKeyColumns", true);
            string validFromName = body["validFromColumn"]?.ToString() ?? "valid_from";
            string validToName = body["validToColumn"]?.ToString() ?? "valid_to";
            string currentName = body["isCurrentColumn"]?.ToString() ?? "is_current";
            string deletedName = body["isDeletedColumn"]?.ToString();
            if (string.IsNullOrWhiteSpace(deletedName) && table.FindColumn("is_deleted") >= 0) deletedName = "is_deleted";
            int validFrom = table.FindColumn(validFromName);
            int validTo = table.FindColumn(validToName);
            int current = string.IsNullOrWhiteSpace(currentName) ? -1 : table.FindColumn(currentName);
            int deleted = string.IsNullOrWhiteSpace(deletedName) ? -1 : table.FindColumn(deletedName);
            if (validFrom < 0 || validTo < 0) throw new DatasetException("INVALID_SCD2_SCHEMA", "The valid-from and valid-to columns must exist in the target table.", null);
            if (!string.IsNullOrWhiteSpace(currentName) && current < 0) throw new DatasetException("INVALID_SCD2_SCHEMA", "The is-current column '" + currentName + "' does not exist.", null);

            JToken snapshotToken = body["snapshotDate"];
            if (snapshotToken == null || snapshotToken.Type == JTokenType.Null) throw new DatasetException("INVALID_REQUEST", "The 'snapshotDate' field is required.", null);
            object snapshot = ApplyAffinity(table.Columns[validFrom], NormalizeJToken(snapshotToken));
            object closeValue = body["closeValue"] == null
                ? snapshot
                : ApplyAffinity(table.Columns[validTo], NormalizeJToken(body["closeValue"]));
            bool absenceMeansDeletion = body["absenceMeansDeletion"]?.ToObject<bool?>() ?? false;
            string deletionMode = body["deletionMode"]?.ToString() ?? "CloseOnly";
            if (!string.Equals(deletionMode, "CloseOnly", StringComparison.OrdinalIgnoreCase) && !string.Equals(deletionMode, "Tombstone", StringComparison.OrdinalIgnoreCase))
                throw new DatasetException("INVALID_REQUEST", "deletionMode must be CloseOnly or Tombstone.", null);
            if (string.Equals(deletionMode, "Tombstone", StringComparison.OrdinalIgnoreCase) && deleted < 0)
                throw new DatasetException("INVALID_SCD2_SCHEMA", "Tombstone mode requires isDeletedColumn.", null);

            List<string> tracked = ReadStringArray(body["trackedColumns"], "trackedColumns", false);
            if (tracked.Count == 0)
            {
                HashSet<string> excluded = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase) { validFromName, validToName };
                if (!string.IsNullOrWhiteSpace(currentName)) excluded.Add(currentName);
                if (!string.IsNullOrWhiteSpace(deletedName)) excluded.Add(deletedName);
                tracked = source.Columns.Select(c => c.Name).Where(n => !excluded.Contains(n) && table.FindColumn(n) >= 0).ToList();
            }
            ValidateColumnList(table, keys);
            ValidateColumnList(table, tracked);
            List<string> keyCollations = keys.Select(k => table.Columns[table.FindColumn(k)].Collation ?? "BINARY").ToList();

            Dictionary<string, SqliteRow> currentRows = new Dictionary<string, SqliteRow>(StringComparer.Ordinal);
            for (int i = 0; i < table.Rows.Count; i++)
            {
                if (!IsCurrent(table.Rows[i], current, validTo)) continue;
                bool anyNull;
                string key = SqliteIntegrityValidator.BuildKey(table, table.Rows[i], keys, keyCollations, out anyNull);
                if (anyNull) throw new DatasetException("NULL_BUSINESS_KEY", "Current rowid " + table.Rows[i].RowId + " has a null business key.", null);
                if (currentRows.ContainsKey(key)) throw new DatasetException("MULTIPLE_CURRENT_VERSIONS", "More than one current row exists for a business key.", new JObject { ["key"] = key });
                currentRows[key] = table.Rows[i];
            }

            SqliteChangeSummary summary = new SqliteChangeSummary { Statements = 1 };
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            for (int r = 0; r < source.Rows.Count; r++)
            {
                _checkBudget();
                object[] mappedNew = MapSourceRow(source, source.Rows[r], table, null);
                SqliteRow keyRow = new SqliteRow { RowId = 0, Values = mappedNew };
                bool anyNull;
                string key = SqliteIntegrityValidator.BuildKey(table, keyRow, keys, keyCollations, out anyNull);
                if (anyNull) throw new DatasetException("NULL_BUSINESS_KEY", "Input row " + r + " has a null business key.", null);
                if (!seen.Add(key)) throw new DatasetException("DUPLICATE_SOURCE_KEY", "The SCD2 input contains a duplicate business key.", new JObject { ["sourceRow"] = r, ["key"] = key });

                SqliteRow existing;
                if (!currentRows.TryGetValue(key, out existing))
                {
                    SetVersionState(mappedNew, validFrom, validTo, current, deleted, snapshot, null, true, false);
                    SqliteRow inserted = CreateInsertedRow(table, mappedNew, "ABORT", true);
                    table.Rows.Add(inserted);
                    currentRows[key] = inserted;
                    summary.Inserted++;
                    continue;
                }

                EnsureSnapshotNotBefore(existing.Values[validFrom], snapshot, key);
                object[] merged = MapSourceRow(source, source.Rows[r], table, existing.Values);
                bool changed = deleted >= 0 && IsTrueValue(existing.Values[deleted]);
                for (int t = 0; t < tracked.Count && !changed; t++)
                {
                    int column = table.FindColumn(tracked[t]);
                    if (!SqliteIntegrityValidator.ValuesEqual(existing.Values[column], merged[column])) changed = true;
                }
                if (!changed) { summary.Unchanged++; continue; }

                if (SqliteIntegrityValidator.ValuesEqual(existing.Values[validFrom], snapshot))
                {
                    existing.Values = merged;
                    SetVersionState(existing.Values, validFrom, validTo, current, deleted, snapshot, null, true, false);
                    if (table.IntegerPrimaryKeyIndex >= 0) existing.Values[table.IntegerPrimaryKeyIndex] = existing.RowId;
                    summary.Updated++;
                    continue;
                }

                CloseVersion(existing, validTo, current, closeValue);
                summary.Updated++;
                summary.ClosedVersions++;
                SetVersionState(merged, validFrom, validTo, current, deleted, snapshot, null, true, false);
                if (table.IntegerPrimaryKeyIndex >= 0) merged[table.IntegerPrimaryKeyIndex] = null;
                SqliteRow next = CreateInsertedRow(table, merged, "ABORT", true);
                table.Rows.Add(next);
                currentRows[key] = next;
                summary.Inserted++;
            }

            if (absenceMeansDeletion)
            {
                foreach (KeyValuePair<string, SqliteRow> pair in currentRows.ToList())
                {
                    _checkBudget();
                    if (seen.Contains(pair.Key)) continue;
                    SqliteRow existing = pair.Value;
                    if (deleted >= 0 && IsTrueValue(existing.Values[deleted])) { summary.Unchanged++; continue; }
                    EnsureSnapshotNotBefore(existing.Values[validFrom], snapshot, pair.Key);
                    if (SqliteIntegrityValidator.ValuesEqual(existing.Values[validFrom], snapshot)
                        && string.Equals(deletionMode, "CloseOnly", StringComparison.OrdinalIgnoreCase))
                    {
                        CloseVersion(existing, validTo, current, closeValue);
                        summary.Updated++; summary.ClosedVersions++;
                        continue;
                    }
                    CloseVersion(existing, validTo, current, closeValue);
                    summary.Updated++; summary.ClosedVersions++;
                    if (string.Equals(deletionMode, "Tombstone", StringComparison.OrdinalIgnoreCase))
                    {
                        object[] tombstone = (object[])existing.Values.Clone();
                        if (table.IntegerPrimaryKeyIndex >= 0) tombstone[table.IntegerPrimaryKeyIndex] = null;
                        SetVersionState(tombstone, validFrom, validTo, current, deleted, snapshot, null, true, true);
                        table.Rows.Add(CreateInsertedRow(table, tombstone, "ABORT", true));
                        summary.Inserted++; summary.Tombstones++;
                    }
                }
            }

            table.Rows.Sort((a, b) => a.RowId.CompareTo(b.RowId));
            ValidateScd2Intervals(table, keys, keyCollations, validFrom, validTo, current);
            EnsureConstraints(table);
            return summary;
        }

        private static void SetVersionState(object[] values, int validFrom, int validTo, int current, int deleted, object from, object to, bool isCurrent, bool isDeleted)
        {
            values[validFrom] = from;
            values[validTo] = to;
            if (current >= 0) values[current] = isCurrent ? 1L : 0L;
            if (deleted >= 0) values[deleted] = isDeleted ? 1L : 0L;
        }

        private static void CloseVersion(SqliteRow row, int validTo, int current, object closeValue)
        {
            row.Values[validTo] = closeValue;
            if (current >= 0) row.Values[current] = 0L;
        }

        private static bool IsCurrent(SqliteRow row, int current, int validTo)
        {
            return current >= 0 ? IsTrueValue(row.Values[current]) : row.Values[validTo] == null;
        }

        private static bool IsTrueValue(object value)
        {
            if (value == null) return false;
            if (value is bool) return (bool)value;
            double number;
            return double.TryParse(value.ToString(), out number) ? number != 0 : string.Equals(value.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        }

        private static void EnsureSnapshotNotBefore(object validFrom, object snapshot, string key)
        {
            if (validFrom == null) return;
            if (CompareTemporal(snapshot, validFrom) < 0)
                throw new DatasetException("OUT_OF_ORDER_SNAPSHOT", "snapshotDate precedes the current version's valid-from value.", new JObject { ["key"] = key, ["snapshotDate"] = ToJToken(snapshot), ["currentValidFrom"] = ToJToken(validFrom) });
        }

        private static int CompareTemporal(object left, object right)
        {
            if (left == null && right == null) return 0;
            if (left == null) return -1;
            if (right == null) return 1;
            DateTime ld, rd;
            if (DateTime.TryParse(left.ToString(), out ld) && DateTime.TryParse(right.ToString(), out rd)) return ld.CompareTo(rd);
            double ln, rn;
            if (double.TryParse(left.ToString(), out ln) && double.TryParse(right.ToString(), out rn)) return ln.CompareTo(rn);
            return string.Compare(left.ToString(), right.ToString(), StringComparison.Ordinal);
        }

        private static void ValidateScd2Intervals(SqliteTable table, List<string> keys, List<string> collations, int validFrom, int validTo, int current)
        {
            Dictionary<string, List<SqliteRow>> groups = new Dictionary<string, List<SqliteRow>>(StringComparer.Ordinal);
            for (int i = 0; i < table.Rows.Count; i++)
            {
                bool anyNull;
                string key = SqliteIntegrityValidator.BuildKey(table, table.Rows[i], keys, collations, out anyNull);
                if (anyNull) continue;
                List<SqliteRow> rows;
                if (!groups.TryGetValue(key, out rows)) groups[key] = rows = new List<SqliteRow>();
                rows.Add(table.Rows[i]);
            }
            foreach (KeyValuePair<string, List<SqliteRow>> group in groups)
            {
                group.Value.Sort((a, b) => CompareTemporal(a.Values[validFrom], b.Values[validFrom]));
                int currents = group.Value.Count(r => IsCurrent(r, current, validTo));
                if (currents > 1) throw new DatasetException("MULTIPLE_CURRENT_VERSIONS", "More than one current row exists after SCD2 processing.", new JObject { ["key"] = group.Key });
                for (int i = 0; i < group.Value.Count; i++)
                {
                    object from = group.Value[i].Values[validFrom], to = group.Value[i].Values[validTo];
                    if (from == null) throw new DatasetException("INVALID_SCD2_INTERVAL", "An SCD2 row has a null valid-from value.", new JObject { ["key"] = group.Key });
                    if (to != null && CompareTemporal(to, from) < 0) throw new DatasetException("INVALID_SCD2_INTERVAL", "An SCD2 row ends before it starts.", new JObject { ["key"] = group.Key });
                    if (i + 1 < group.Value.Count && to != null && CompareTemporal(to, group.Value[i + 1].Values[validFrom]) > 0)
                        throw new DatasetException("OVERLAPPING_SCD2_INTERVALS", "SCD2 validity intervals overlap.", new JObject { ["key"] = group.Key });
                }
            }
        }

        private static List<string> SplitStatements(string sql)
        {
            List<string> result = new List<string>();
            StringBuilder current = new StringBuilder();
            char quote = '\0';
            bool lineComment = false, blockComment = false;
            for (int i = 0; i < sql.Length; i++)
            {
                char ch = sql[i];
                if (lineComment)
                {
                    current.Append(ch);
                    if (ch == '\n' || ch == '\r') lineComment = false;
                    continue;
                }
                if (blockComment)
                {
                    current.Append(ch);
                    if (ch == '*' && i + 1 < sql.Length && sql[i + 1] == '/') { current.Append('/'); i++; blockComment = false; }
                    continue;
                }
                if (quote != '\0')
                {
                    current.Append(ch);
                    if (ch == quote)
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == quote) current.Append(sql[++i]);
                        else quote = '\0';
                    }
                    else if (quote == ']' && ch == ']') quote = '\0';
                    continue;
                }
                if (ch == '-' && i + 1 < sql.Length && sql[i + 1] == '-') { current.Append("--"); i++; lineComment = true; continue; }
                if (ch == '/' && i + 1 < sql.Length && sql[i + 1] == '*') { current.Append("/*"); i++; blockComment = true; continue; }
                if (ch == '\'' || ch == '"' || ch == (char)96) { quote = ch; current.Append(ch); continue; }
                if (ch == '[') { quote = ']'; current.Append(ch); continue; }
                if (ch == ';')
                {
                    if (!string.IsNullOrWhiteSpace(current.ToString())) result.Add(current.ToString().Trim());
                    current.Length = 0;
                }
                else current.Append(ch);
            }
            if (quote != '\0' || blockComment) throw new DatasetException("SQL_PARSE_ERROR", "The SQL batch ends inside a quote or comment.", null);
            if (!string.IsNullOrWhiteSpace(current.ToString())) result.Add(current.ToString().Trim());
            return result;
        }

        private static bool StartsWithWord(string text, string word)
        {
            int p = 0;
            SkipWhitespace(text, ref p);
            return p + word.Length <= text.Length && string.Compare(text, p, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0
                && (p + word.Length == text.Length || !IsWordChar(text[p + word.Length]));
        }

        private static string FirstWord(string text)
        {
            int p = 0;
            return ReadWord(text, ref p);
        }

        private static void ExpectWord(string text, ref int position, string expected)
        {
            string actual = ReadWord(text, ref position);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new DatasetException("SQL_PARSE_ERROR", "Expected " + expected + " but found " + (actual ?? "end of statement") + ".", new JObject { ["position"] = position });
        }

        private static bool TryConsumeWord(string text, ref int position, string word)
        {
            int saved = position;
            string actual = ReadWord(text, ref position);
            if (string.Equals(actual, word, StringComparison.OrdinalIgnoreCase)) return true;
            position = saved;
            return false;
        }

        private static string ReadWord(string text, ref int position)
        {
            SkipWhitespace(text, ref position);
            int start = position;
            while (position < text.Length && IsWordChar(text[position])) position++;
            return position == start ? null : text.Substring(start, position - start);
        }

        private static void SkipWhitespace(string text, ref int position)
        {
            while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
        }

        private static void SkipWhitespaceAndComma(string text, ref int position)
        {
            while (position < text.Length && (char.IsWhiteSpace(text[position]) || text[position] == ',')) position++;
        }

        private static bool IsWordChar(char value) { return char.IsLetterOrDigit(value) || value == '_'; }

        private static int FindTopLevelEquals(string text)
        {
            int depth = 0;
            char quote = '\0';
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (quote != '\0')
                {
                    if (ch == quote)
                    {
                        if (i + 1 < text.Length && text[i + 1] == quote) i++;
                        else quote = '\0';
                    }
                    else if (quote == ']' && ch == ']') quote = '\0';
                    continue;
                }
                if (ch == '\'' || ch == '"' || ch == (char)96) { quote = ch; continue; }
                if (ch == '[') { quote = ']'; continue; }
                if (ch == '(') depth++;
                else if (ch == ')') depth--;
                else if (ch == '=' && depth == 0) return i;
            }
            return -1;
        }

        private static string QuoteIdentifier(string name) { return "\"" + name.Replace("\"", "\"\"") + "\""; }
    }

}
