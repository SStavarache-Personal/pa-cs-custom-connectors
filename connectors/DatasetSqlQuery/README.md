# Dataset SQL Query

Run read-only SQL queries against one or more user-provided datasets in Power Automate custom connector code.

## Overview

`Dataset SQL Query` ingests datasets provided at request time (`Json` or `Csv`) and executes a SQL query fully in `script.csx`.

### Key capabilities

- Multiple datasets per request
- User-defined dataset names (table names in SQL)
- JSON and CSV input support
- CSV parser supports custom separator, quote char, line break mode, and all-columns-as-string mode
- SQL support for:
  - `SELECT`, `WHERE`, `JOIN` (`INNER`, `LEFT`, `RIGHT`, `FULL`, `CROSS`)
  - `GROUP BY`, `HAVING`, aggregates (`COUNT`, `SUM`, `AVG`, `MIN`, `MAX`)
  - `ORDER BY`, `LIMIT`, `OFFSET`
  - CTEs (`WITH`)
  - Subqueries (in `FROM` and scalar/in-list contexts)
  - Set operators (`UNION`, `UNION ALL`, `INTERSECT`, `EXCEPT`)
  - Window functions (`ROW_NUMBER`, `RANK`, `DENSE_RANK`, `LAG`, `LEAD`, and aggregate windows)
  - Common transformation functions (`LOWER`, `UPPER`, `TRIM`, `SUBSTR`, `REPLACE`, `CONCAT`, `ROUND`, `COALESCE`, `NULLIF`, `STRFTIME`, etc.)
- Output modes:
  - `Csv`
  - `RowsAndSchema`
  - `ObjectArray`

## Operation

| Operation | Description |
|-----------|-------------|
| `ExecuteDatasetSqlQuery` | Executes a read-only SQL query against provided datasets. |

## Request contract

### Required fields

- `sql`: SQL statement
- `outputMode`: `Csv` / `RowsAndSchema` / `ObjectArray`
- `datasets`: Array of dataset objects

### Dataset object

- `name`: table name used in SQL
- `format`: `Json` or `Csv`
- `jsonRows`: array of objects (required for `Json`)
- `csvText`: CSV content (required for `Csv`)
- `csvOptions` (optional):
  - `separator`
  - `quote`
  - `lineBreak` (`Auto`, `LF`, `CRLF`, `CR`)
  - `firstRowIsHeader`
  - `allColumnsAsString`
- `typeOverrides` (optional): map of column name to type (`String`, `Int64`, `Double`, `Decimal`, `Boolean`, `DateTime`, `Null`)

### Engine options

- `caseSensitiveIdentifiers` (default `false`)
- `nullsSort` (`First`/`Last`, default `Last`)
- `maxOutputRows` (default `100000`, capped internally)

## Response

All successful responses return:

- `mode`
- `rowCount`
- `durationMs`
- `result` (shape depends on output mode)

## Performance notes

- The engine runs entirely in memory within Power Platform custom code runtime.
- It is optimized for moderate-sized datasets and analytical queries.
- For very large datasets or very complex SQL, use `maxOutputRows` and tighter filters to avoid timeout/resource limits.

## Limitations

- Read-only SQL only (`SELECT` workflows).
- DML/DDL statements are not supported.
- Some advanced SQL dialect-specific features are not implemented.
- In this version, combining grouped aggregates and window functions in the same `SELECT` is not supported.

## Error codes

- `INVALID_REQUEST`
- `INVALID_DATASET`
- `CSV_PARSE_ERROR`
- `SQL_PARSE_ERROR`
- `SQL_VALIDATION_ERROR`
- `UNSUPPORTED_SQL_FEATURE`
- `RESULT_LIMIT_EXCEEDED`
- `EXECUTION_ERROR`

## Deployment

Deploy with:

```powershell
.\deploy-connector.ps1 -ConnectorName "DatasetSqlQuery"
```
