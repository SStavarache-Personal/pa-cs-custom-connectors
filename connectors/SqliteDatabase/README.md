# SQLite Database

Read and update a user-supplied SQLite database file entirely inside a Power Automate C# custom connector. The connector does not use `System.Data`, native interop, NuGet packages, an external API, or an on-premises gateway. It implements the required SQLite file-format, b-tree, record, overflow, SQL, mutation, and serialization behavior in the single `script.csx` file allowed by the platform.

## Operations

| Operation | Purpose | Output |
|---|---|---|
| `QuerySqlite` | Run one read-only SQL query, including joins and aggregation | One table as row arrays plus schema, object rows, or CSV |
| `ExecuteSqlite` | Run a batch of `INSERT`, `UPDATE`, `DELETE`, and supported `PRAGMA` statements | The complete rebuilt database file |
| `BulkUpsertSqlite` | Insert/update JSON or CSV rows with an O(n) business-key lookup | The complete rebuilt database file |
| `ApplyScd2Sqlite` | Apply a stateful Type 2 slowly changing dimension snapshot | The complete rebuilt database file |
| `InspectSqlite` | Parse the header, schema, tables, indexes, and logical constraints | Metadata, schema, and validation details |

Every write is all-or-nothing from the flow's perspective: mutations occur in memory, no database bytes are returned on an error, and a successful response contains a newly serialized complete database.

## File-format implementation

The reader supports the parts of the SQLite 3 file format needed by ordinary rowid tables:

- page sizes from 512 through 65,536 bytes;
- UTF-8, UTF-16LE, and UTF-16BE database encodings;
- table interior and leaf b-trees of arbitrary supported depth;
- all SQLite record serial types, signed rowids, BLOBs, and overflow chains;
- `INTEGER PRIMARY KEY` rowid aliases;
- `sqlite_schema`, automatic indexes, and simple explicit column indexes;
- built-in `BINARY`, ASCII `NOCASE`, and `RTRIM` index collations;
- ascending and descending multi-column indexes.

The writer creates a compact fresh page graph rather than trying to edit arbitrary free space in place. It rebuilds every supported table and index, writes a new `sqlite_schema` b-tree, preserves the original page size and text encoding, updates the database header, reopens its own output, and compares every materialized row, value, rowid, and schema object with the intended in-memory state.

This approach deliberately avoids returning a half-modified file. It is connector-payload atomicity, not a substitute for SQLite's filesystem journal/WAL atomic-commit protocol.

## Query SQL

`QuerySqlite` uses the repository's in-memory SQL engine over tables materialized from the database. Supported features include:

- `SELECT`, aliases, `DISTINCT`, `WHERE`, `GROUP BY`, `HAVING`, `ORDER BY`, `LIMIT`, and `OFFSET`;
- CTEs and scalar/`IN` subqueries;
- `INNER`, `LEFT`, `RIGHT`, `FULL`, `CROSS`, `LEFT ANTI`, and `RIGHT ANTI` joins;
- hash acceleration for cross-table equi-join predicates, including an equi-key inside an `AND` predicate;
- `UNION`, `UNION ALL`, `INTERSECT`, and `EXCEPT`;
- `COUNT`, `SUM`, `AVG`, `MIN`, and `MAX`, including supported `DISTINCT` forms;
- `ROW_NUMBER`, `RANK`, and `DENSE_RANK` windows;
- `CASE`, `CAST`, `IN`, `BETWEEN`, `LIKE`, null predicates, arithmetic, and boolean expressions;
- common text, numeric, null, date/time, and `STRFTIME` scalar functions.

Named `@name`, `:name`, and `$name` parameters are accepted through the `parameters` object. Only scalar JSON values are allowed, and parameter text is emitted as a quoted SQL literal so it cannot alter the SQL structure.

Example:

```json
{
  "databaseBase64": "<file content>",
  "sql": "SELECT c.region, COUNT(*) AS orders, SUM(o.total) AS sales FROM orders o JOIN customers c ON o.customer_id=c.id WHERE o.order_date >= @from GROUP BY c.region ORDER BY sales DESC",
  "parameters": {
    "from": "2026-01-01"
  },
  "outputMode": "ObjectArray"
}
```

BLOB values in JSON query output are base64 strings. CSV output uses RFC-style quoting for commas, quotes, and line breaks.

## SQL writes

`ExecuteSqlite` accepts semicolon-delimited:

- `INSERT [OR ABORT|IGNORE|REPLACE] ... VALUES (...)`;
- `INSERT ... SELECT ...`;
- `UPDATE ... SET ... [WHERE ...]`;
- `DELETE FROM ... [WHERE ...]`;
- `PRAGMA user_version = N` and `PRAGMA application_id = N`;
- optional `BEGIN`, `COMMIT`, or `END` wrappers.

DDL and SQL `ON CONFLICT ... DO UPDATE` are not implemented. Use an existing schema and `BulkUpsertSqlite` for efficient upserts. The mutation layer applies SQLite-style column affinity, scalar/default expressions supported by the SQL engine, `CURRENT_DATE`, `CURRENT_TIME`, `CURRENT_TIMESTAMP`, `INTEGER PRIMARY KEY`, and `AUTOINCREMENT`/`sqlite_sequence` allocation.

Example:

```json
{
  "databaseBase64": "<file content>",
  "sql": "BEGIN; UPDATE inventory SET quantity=quantity-@used WHERE sku=@sku; INSERT INTO audit_log(sku,event) VALUES(@sku,'consumed'); PRAGMA user_version=8; COMMIT;",
  "parameters": {
    "sku": "SKU-100",
    "used": 2
  }
}
```

## Bulk upsert

`BulkUpsertSqlite` builds one dictionary for the target key and one set for source keys. Inserts use a cached rowid allocator, while final constraint validation is linear per supported unique index. This avoids per-source-row table scans.

```json
{
  "databaseBase64": "<file content>",
  "table": "customer",
  "format": "Json",
  "jsonRows": [
    { "customer_code": "C001", "name": "Ada", "segment": "Enterprise" },
    { "customer_code": "C002", "name": "Lin", "segment": "SMB" }
  ],
  "keyColumns": ["customer_code"],
  "updateColumns": ["name", "segment"],
  "mode": "Upsert",
  "nullUpdateBehavior": "IgnoreNulls"
}
```

Input formats are `Json` and `Csv`. The CSV parser supports a configurable one-character separator and quote, LF/CRLF/CR detection, escaped quotes, and line breaks inside quoted fields. `mode` can be `Upsert`, `InsertOnly`, or `UpdateOnly`.

## SCD Type 2

The dimension table must already contain:

- one or more stable business-key columns;
- a valid-from column and a nullable valid-to column;
- optionally, an is-current flag;
- optionally, an is-deleted flag for tombstones;
- normally, an integer surrogate key and a unique constraint on `(business key..., valid_from)`.

For each snapshot row, `ApplyScd2Sqlite` finds the current member in O(1) average time. An unchanged member is retained. A changed member is closed and a new current version is inserted. A new member is inserted. With `absenceMeansDeletion=true`, absent current members are either closed or closed plus a new deleted current tombstone.

```json
{
  "databaseBase64": "<file content>",
  "table": "dim_product",
  "format": "Json",
  "jsonRows": [
    { "sku": "A-1", "name": "Widget", "price": 12.5 },
    { "sku": "B-2", "name": "Gadget", "price": 20.0 }
  ],
  "businessKeyColumns": ["sku"],
  "trackedColumns": ["name", "price"],
  "snapshotDate": "2026-06-30T00:00:00Z",
  "validFromColumn": "valid_from",
  "validToColumn": "valid_to",
  "isCurrentColumn": "is_current",
  "isDeletedColumn": "is_deleted",
  "absenceMeansDeletion": true,
  "deletionMode": "Tombstone"
}
```

The implementation also:

- rejects duplicate/null business keys and multiple current versions;
- rejects snapshots older than a current version;
- treats a changed row at the same effective date as an in-place correction instead of creating a zero-length duplicate version;
- verifies that final validity intervals do not overlap;
- is idempotent when the same snapshot is applied again.

`closeValue` defaults to `snapshotDate`, giving half-open intervals `[valid_from, valid_to)`. Supply a different value only when your warehouse convention requires it.

## Validation and safety

A successful write reports `integrity.ok=true` only after these checks:

1. input header, page boundaries, b-tree page types, cell pointers, varints, payload sizes, and overflow chains were parsed safely;
2. rowids, record column counts, `NOT NULL`, supported unique indexes, and parsed foreign-key references are valid;
3. SCD2 current-row and interval invariants are valid when applicable;
4. every supported table and index was serialized;
5. the emitted bytes were reopened through the connector's reader;
6. reopened schema objects, tables, rowids, values (including BLOB bytes), and row counts exactly match the intended state.

The automated tests additionally open every emitted file with an independent native SQLite engine and require both `PRAGMA integrity_check` and `PRAGMA foreign_key_check` to return no errors.

### WAL inputs

Read operations can inspect a main file marked for WAL mode, but the response warns that a separate, unprovided `-wal` file may contain newer committed pages. Writes reject WAL-mode main files by default. Only set `limits.allowWalSnapshot=true` after checkpointing and supplying the current main database file. The rebuilt output uses rollback-journal header mode.

## Resource guards

The code reserves ten seconds of the platform's two-minute ceiling and returns `TIME_BUDGET_EXCEEDED` after 110 seconds. Defaults and hard maxima are:

| Guard | Default | Hard maximum |
|---|---:|---:|
| Decoded input / rebuilt output | 32 MiB | 64 MiB |
| Materialized rows across tables | 500,000 | 2,000,000 |
| Pages | 131,072 | 262,144 |
| SQL statements per batch | 100 | 1,000 |
| Query output rows | 100,000 | 1,000,000 |

Base64 increases payload size by about one third, and Power Platform request/response limits can be lower than the connector's hard guards. Choose database and batch sizes with margin for JSON serialization and the flow's other actions.

On the repository test fixture, the local release-equivalent runner processes a 20,000-row upsert (10,000 updates plus 10,000 inserts) and a 10,000-key SCD2 snapshot in well under the 110-second budget. These are regression benchmarks, not guarantees for every Power Platform region or database shape.

## Deliberate limitations

The connector rejects or does not materialize structures whose semantics cannot be reproduced safely with the Power Automate namespace/runtime restrictions:

- `WITHOUT ROWID`, virtual, and generated-column tables;
- querying stored views as sources (their schema SQL is preserved, but only ordinary tables are materialized);
- schema changes (`CREATE`, `ALTER`, `DROP`) during a write;
- triggers during writes, because the in-memory engine cannot execute trigger programs;
- writes to databases with `CHECK` constraints or `STRICT` tables;
- partial indexes, expression indexes, and custom collations during writes;
- writes with reserved page bytes or auto-vacuum pointer maps;
- native SQLite extensions, FTS, R-Tree, application-defined functions, and extensions loaded from shared libraries;
- automatic execution of foreign-key cascade actions. Final parsed foreign-key references are validated and a violating mutation is rejected.

These cases fail with stable structured error codes rather than returning bytes whose semantics are uncertain.

## Tests

The connector includes:

- a .NET Standard 2.0/C# 7.3 compile project;
- a local runtime shim and executable runner;
- an end-to-end Python standard-library suite that generates SQLite fixtures and uses Python's independent SQLite library as the output oracle;
- cases for deep 512-byte-page b-trees, overflow payloads, automatic and explicit indexes, UTF-8/UTF-16, 65,536-byte pages, negative and large rowids, BLOBs, defaults, `AUTOINCREMENT`, joins, CTEs, windows, parameter quoting, SQL DML, JSON/CSV bulk input, SCD2 idempotence/corrections/deletions, WAL safety, malformed files, and unsupported structures;
- 20,000-row bulk and 10,000-key SCD2 regression benchmarks.

Build and run locally:

```powershell
dotnet build testing/SqliteDatabaseCompile/SqliteDatabaseCompile.csproj
dotnet build testing/SqliteDatabaseRunner/SqliteDatabaseRunner.csproj
python testing/SqliteDatabaseTests/run_tests.py
```

The script must remain below 1 MB and compile for .NET Standard 2.0 with only the namespaces allowed by Power Automate custom code.

## Deployment

Deploy with the repository helper:

```powershell
.\deploy-connector.ps1 -ConnectorName "SqliteDatabase"
```

The connector has no connection parameters because all database content is supplied per action. Replace the placeholder host if your environment's connector-import workflow requires one; the scripted operations do not call that host.

## References

- [Microsoft: Write code in a custom connector](https://learn.microsoft.com/en-us/connectors/custom-connectors/write-code)
- [SQLite database file format](https://www.sqlite.org/fileformat2.html)
- [SQLite atomic commit](https://sqlite.org/atomiccommit.html)
- [Driverless Power Query SQLite reader used as a format-reference cross-check](https://github.com/SStavarache-Personal/powerquery-driverless/tree/main/sqlite3)
