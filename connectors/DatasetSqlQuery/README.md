# Dataset SQL Operations

Transform user-provided datasets inside Power Automate custom connector code with reusable SQL-style actions and a raw SQL action.

## Overview

`Dataset SQL Operations` accepts request-time datasets in `Json` or `Csv` format, normalizes them in memory, and runs the requested transformation through the connector SQL engine in `script.csx`.

### Key capabilities

- JSON and CSV input support across all actions
- Optional column type overrides for strings, numbers, booleans, dates, datetimes, and timestamps
- Raw SQL execution for advanced scenarios
- Shortcut actions for the main transformation steps used in flows:
  - Select
  - Filter
  - Join
  - Group By
  - Distinct
  - Sort
  - Union
  - Union All
- SQL support for joins, grouping, ordering, set operators, CTEs, subqueries, and ranking/window functions already supported by the engine
- Output modes:
  - `Csv`
  - `RowsAndSchema`
  - `ObjectArray`

## Operations

| Operation | Description |
|-----------|-------------|
| `ExecuteDatasetSqlQuery` | Runs a raw read-only SQL query against zero or more datasets. `outputMode` defaults to `RowsAndSchema`, and `datasets` can be omitted for dataset-less queries such as `SELECT 1`. |
| `SelectDatasetRows` | Projects a single dataset using a SQL-style column list with optional aliases. |
| `FilterDatasetRows` | Filters a single dataset with a SQL `WHERE` expression. `CAST(... AS DATE|DATETIME|TIMESTAMP)` is supported for date/time comparisons. |
| `JoinDatasets` | Joins two datasets using `Inner`, `Left`, `Right`, `LeftAnti`, or `RightAnti`. |
| `GroupDatasetRows` | Groups a dataset with multiple aggregate expressions plus optional ranking expressions (`ROW_NUMBER`, `RANK`, `DENSE_RANK`). |
| `DistinctDatasetRows` | Returns distinct rows for either selected columns or the whole dataset. |
| `SortDatasetRows` | Sorts a dataset with a SQL-style `ORDER BY` expression. |
| `UnionDatasets` | Combines two or more datasets with `UNION`. |
| `UnionAllDatasets` | Combines two or more datasets with `UNION ALL`. |

## Shared dataset contract

Each action uses the same dataset object shape.

- `name`: table name used in generated SQL
- `format`: `Json` or `Csv`
- `jsonRows`: array of objects (required for `Json`)
- `csvText`: CSV content (required for `Csv`)
- `csvOptions` (optional):
  - `separator`
  - `quote`
  - `lineBreak` (`Auto`, `LF`, `CRLF`, `CR`)
  - `firstRowIsHeader`
  - `allColumnsAsString`
- `typeOverrides` (optional): map of column name to type (`String`, `Int64`, `Double`, `Decimal`, `Boolean`, `Date`, `DateTime`, `Timestamp`, `Null`). `Number` and `Numeric` are aliases for `Double`, and `Timestamp` is an alias for `DateTime`.

## Representative requests

### Raw SQL with no datasets

```json
{
  "sql": "select 1 as sample_value"
}
```

### Select

```json
{
  "dataset": {
    "name": "orders",
    "format": "Json",
    "jsonRows": [
      { "order_id": 1, "user_id": 7, "order_total": 25.5 }
    ]
  },
  "columns": "order_id, user_id as customer_id, order_total",
  "outputMode": "ObjectArray"
}
```

### Filter with CAST

```json
{
  "dataset": {
    "name": "orders",
    "format": "Csv",
    "csvText": "user_id,order_date,order_total\n1,2025-01-02,10\n2,2024-12-31,12"
  },
  "where": "user_id in (1, 2, 3) and cast(order_date as date) > cast('2025-01-01' as date)",
  "outputMode": "ObjectArray"
}
```

### Join

```json
{
  "leftDataset": {
    "name": "orders",
    "format": "Json",
    "jsonRows": [
      { "order_id": 1, "user_id": 7, "order_total": 25.5 }
    ]
  },
  "rightDataset": {
    "name": "users",
    "format": "Json",
    "jsonRows": [
      { "user_id": 7, "user_name": "Adele" }
    ]
  },
  "joinType": "Left",
  "on": "orders.user_id = users.user_id",
  "selectColumns": "orders.order_id, users.user_name, orders.order_total"
}
```

### Group by with aggregates and ranking

```json
{
  "dataset": {
    "name": "orders",
    "format": "Json",
    "jsonRows": [
      { "region": "North", "sales_rep": "Ana", "order_total": 10 },
      { "region": "North", "sales_rep": "Ana", "order_total": 15 },
      { "region": "North", "sales_rep": "Ben", "order_total": 40 }
    ]
  },
  "groupByColumns": "region, sales_rep",
  "aggregations": [
    { "function": "COUNT", "alias": "order_count" },
    { "function": "SUM", "column": "order_total", "alias": "total_sales" },
    { "function": "AVG", "column": "order_total", "alias": "avg_sales" }
  ],
  "rankings": [
    { "function": "RANK", "alias": "sales_rank", "orderBy": "total_sales desc", "partitionByColumns": "region" }
  ],
  "orderBy": "region asc, sales_rank asc"
}
```

### Distinct

```json
{
  "dataset": {
    "name": "orders",
    "format": "Json",
    "jsonRows": [
      { "user_id": 7, "region": "North" },
      { "user_id": 7, "region": "North" },
      { "user_id": 8, "region": "South" }
    ]
  },
  "columns": "user_id, region"
}
```

### Sort

```json
{
  "dataset": {
    "name": "orders",
    "format": "Json",
    "jsonRows": [
      { "user_id": 7, "order_date": "2025-01-02" },
      { "user_id": 7, "order_date": "2025-01-01" }
    ]
  },
  "orderBy": "user_id desc, order_date asc"
}
```

### Union / Union All

```json
{
  "datasets": [
    {
      "name": "jan_orders",
      "format": "Json",
      "jsonRows": [
        { "order_id": 1, "user_id": 7 }
      ]
    },
    {
      "name": "feb_orders",
      "format": "Json",
      "jsonRows": [
        { "order_id": 2, "user_id": 8 }
      ]
    }
  ],
  "columns": "order_id, user_id"
}
```

Use the same request body with either `UnionDatasets` or `UnionAllDatasets` depending on whether duplicates should be removed.

## Response

All successful responses return:

- `mode`
- `rowCount`
- `durationMs`
- `result`

`result` is shaped by `outputMode`:

- `Csv`: `{ "csv": "..." }`
- `RowsAndSchema`: `{ "schema": [...], "rows": [[...], ...] }`
- `ObjectArray`: `{ "rows": [{...}, ...] }`

## Notes and limitations

- Read-only transformations only.
- Dataset names must be valid SQL identifiers (`[A-Za-z_][A-Za-z0-9_]*`).
- Anti joins return only the preserved side (`leftDataset` for `LeftAnti`, `rightDataset` for `RightAnti`).
- The ranking helpers currently support `ROW_NUMBER`, `RANK`, and `DENSE_RANK`.
- The engine runs entirely in memory and should be used with moderate dataset sizes to stay inside Power Platform limits.

## Deployment

Deploy with:

```powershell
.\deploy-connector.ps1 -ConnectorName "DatasetSqlQuery"
```
