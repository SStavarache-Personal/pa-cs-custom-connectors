#!/usr/bin/env python3
"""End-to-end tests for the Power Automate SQLite connector.

The standard-library sqlite3 module is deliberately used as an independent
oracle.  Every database emitted by the connector is opened by SQLite itself
and checked with PRAGMA integrity_check before its logical results are tested.
"""

from __future__ import annotations

import argparse
import base64
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile
import time


ROOT = Path(__file__).resolve().parents[2]
DEFAULT_RUNNER = ROOT / "testing/SqliteDatabaseRunner/bin/Debug/net8.0/SqliteDatabaseRunner.dll"
CONNECTOR = ROOT / "connectors/SqliteDatabase"


class TestFailure(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise TestFailure(message)


def test_connector_assets() -> None:
    required = ("apiDefinition.swagger.json", "apiProperties.json", "script.csx", "README.md")
    for name in required:
        require((CONNECTOR / name).is_file(), f"missing connector asset: {name}")
    script_path = CONNECTOR / "script.csx"
    script = script_path.read_text(encoding="utf-8")
    require(script_path.stat().st_size < 1_000_000, "script.csx exceeds the 1 MB platform limit")
    require("public class Script : ScriptBase" in script, "Script does not inherit ScriptBase")
    require("public override async Task<HttpResponseMessage> ExecuteAsync()" in script, "ExecuteAsync entry point is missing")

    allowed_namespaces = {
        "System", "System.Collections", "System.Collections.Generic", "System.Diagnostics",
        "System.IO", "System.IO.Compression", "System.Linq", "System.Net", "System.Net.Http",
        "System.Net.Http.Headers", "System.Net.Security", "System.Security.Authentication",
        "System.Security.Cryptography", "System.Text", "System.Text.RegularExpressions",
        "System.Threading", "System.Threading.Tasks", "System.Web", "System.Xml", "System.Xml.Linq",
        "System.Drawing", "System.Drawing.Drawing2D", "System.Drawing.Imaging",
        "Microsoft.Extensions.Logging", "Newtonsoft.Json", "Newtonsoft.Json.Linq",
    }
    namespaces = {
        line[len("using ") : -1].strip()
        for line in script.splitlines()
        if line.startswith("using ") and line.endswith(";")
    }
    require(namespaces <= allowed_namespaces, f"unsupported using directives: {sorted(namespaces - allowed_namespaces)}")

    swagger = json.loads((CONNECTOR / "apiDefinition.swagger.json").read_text(encoding="utf-8"))
    properties = json.loads((CONNECTOR / "apiProperties.json").read_text(encoding="utf-8"))
    require(swagger.get("swagger") == "2.0", "connector must use Swagger/OpenAPI 2.0")
    require(len(swagger.get("info", {}).get("title", "")) <= 30, "connector title exceeds 30 characters")
    operations = {
        operation["operationId"]
        for path in swagger.get("paths", {}).values()
        for operation in path.values()
        if isinstance(operation, dict) and "operationId" in operation
    }
    scripted = set(properties.get("properties", {}).get("scriptOperations", []))
    require(operations == scripted, f"Swagger/scriptOperations mismatch: {operations ^ scripted}")
    for operation in operations:
        require(f'case "{operation}"' in script, f"script does not dispatch {operation}")
    definitions = swagger.get("definitions", {})

    def visit(value: object) -> None:
        if isinstance(value, dict):
            reference = value.get("$ref")
            if isinstance(reference, str) and reference.startswith("#/definitions/"):
                require(reference.split("/")[-1] in definitions, f"unresolved Swagger reference: {reference}")
            for child in value.values():
                visit(child)
        elif isinstance(value, list):
            for child in value:
                visit(child)

    visit(swagger)
    require(len(list(CONNECTOR.glob("*.csx"))) == 1, "connector must contain exactly one C# script")
    print(f"PASS connector asset contract  {script_path.stat().st_size} script bytes")


def make_core_database(path: Path, customer_count: int = 240, sale_count: int = 4200) -> None:
    connection = sqlite3.connect(path)
    connection.executescript(
        """
        PRAGMA page_size = 512;
        PRAGMA journal_mode = DELETE;
        VACUUM;
        CREATE TABLE customers (
          id INTEGER PRIMARY KEY,
          code TEXT NOT NULL COLLATE NOCASE UNIQUE,
          name TEXT NOT NULL,
          region TEXT,
          score REAL DEFAULT 0
        );
        CREATE TABLE products (
          id INTEGER PRIMARY KEY,
          sku TEXT NOT NULL UNIQUE,
          name TEXT NOT NULL,
          price REAL,
          payload BLOB
        );
        CREATE TABLE sales (
          id INTEGER PRIMARY KEY,
          customer_id INTEGER NOT NULL,
          product_id INTEGER NOT NULL,
          quantity INTEGER NOT NULL,
          sale_date TEXT NOT NULL,
          note TEXT,
          FOREIGN KEY (customer_id) REFERENCES customers(id),
          FOREIGN KEY (product_id) REFERENCES products(id)
        );
        CREATE INDEX idx_sales_customer_date
          ON sales(customer_id, sale_date DESC);
        CREATE TABLE dim_product (
          product_key INTEGER PRIMARY KEY,
          sku TEXT NOT NULL,
          name TEXT,
          price REAL,
          valid_from TEXT NOT NULL,
          valid_to TEXT,
          is_current INTEGER NOT NULL,
          is_deleted INTEGER NOT NULL,
          UNIQUE (sku, valid_from)
        );
        CREATE TABLE events (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          code TEXT NOT NULL UNIQUE,
          value TEXT NOT NULL DEFAULT 'seed'
        );
        """
    )
    regions = ("North", "South", "East", "West")
    connection.executemany(
        "INSERT INTO customers(id, code, name, region, score) VALUES (?, ?, ?, ?, ?)",
        [
            (i, f"C{i:05d}", f"Customer {i}", regions[i % len(regions)], i / 10.0)
            for i in range(1, customer_count + 1)
        ],
    )
    connection.executemany(
        "INSERT INTO events(code, value) VALUES (?, ?)",
        [("EVT-1", "one"), ("EVT-2", "two")],
    )
    connection.execute("DELETE FROM events WHERE id = 2")
    connection.execute("UPDATE sqlite_sequence SET seq = 50 WHERE name = 'events'")
    connection.executemany(
        "INSERT INTO products(id, sku, name, price, payload) VALUES (?, ?, ?, ?, ?)",
        [
            (i, f"SKU{i:04d}", f"Product {i}", i * 1.25, bytes((i + j) % 256 for j in range(48)))
            for i in range(1, 81)
        ],
    )
    rows = []
    for i in range(1, sale_count + 1):
        note = ("overflow-" + str(i) + "-") * (180 if i % 997 == 0 else 1)
        rows.append(
            (
                i,
                (i % customer_count) + 1,
                (i % 80) + 1,
                (i % 7) + 1,
                f"2026-{(i % 12) + 1:02d}-{(i % 28) + 1:02d}",
                note,
            )
        )
    connection.executemany(
        "INSERT INTO sales(id, customer_id, product_id, quantity, sale_date, note) VALUES (?, ?, ?, ?, ?, ?)",
        rows,
    )
    connection.executemany(
        "INSERT INTO dim_product(product_key, sku, name, price, valid_from, valid_to, is_current, is_deleted) VALUES (?, ?, ?, ?, ?, NULL, 1, 0)",
        [
            (1, "SKU0001", "Widget", 10.0, "2025-01-01T00:00:00Z"),
            (2, "SKU0002", "Gadget", 20.0, "2025-01-01T00:00:00Z"),
        ],
    )
    connection.execute("PRAGMA user_version = 7")
    connection.execute("PRAGMA application_id = 1347638089")
    connection.commit()
    connection.close()


def make_variant_database(path: Path, page_size: int, encoding: str) -> None:
    connection = sqlite3.connect(path)
    connection.execute(f"PRAGMA page_size = {page_size}")
    connection.execute(f"PRAGMA encoding = '{encoding}'")
    connection.execute("VACUUM")
    connection.executescript(
        """
        CREATE TABLE edge_values (
          id INTEGER PRIMARY KEY,
          label TEXT NOT NULL COLLATE NOCASE UNIQUE,
          amount NUMERIC,
          payload BLOB
        );
        CREATE INDEX idx_edge_amount_desc ON edge_values(amount DESC);
        CREATE TABLE empty_table(id INTEGER PRIMARY KEY, value TEXT);
        """
    )
    connection.executemany(
        "INSERT INTO edge_values(id, label, amount, payload) VALUES (?, ?, ?, ?)",
        [
            (-5, "Ångström 東京", -9223372036854775807, bytes(range(256)) * 40),
            (1, "Résumé", 0.125, b""),
            (9223372036854775806, "Zulu", None, b"binary\x00payload"),
        ],
    )
    connection.commit()
    connection.close()


def make_bulk_database(path: Path, existing_rows: int = 10000) -> None:
    connection = sqlite3.connect(path)
    connection.executescript(
        """
        PRAGMA page_size = 4096;
        VACUUM;
        CREATE TABLE bulk_target (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          code TEXT NOT NULL UNIQUE,
          metric INTEGER,
          description TEXT
        );
        """
    )
    connection.executemany(
        "INSERT INTO bulk_target(code, metric, description) VALUES (?, ?, ?)",
        [(f"B{i:06d}", i, f"old-{i}") for i in range(existing_rows)],
    )
    connection.commit()
    connection.close()


def make_scd_database(path: Path, existing_rows: int = 10000) -> None:
    connection = sqlite3.connect(path)
    connection.executescript(
        """
        CREATE TABLE dimension (
          version_key INTEGER PRIMARY KEY AUTOINCREMENT,
          business_code TEXT NOT NULL,
          attribute TEXT,
          metric REAL,
          valid_from TEXT NOT NULL,
          valid_to TEXT,
          is_current INTEGER NOT NULL,
          is_deleted INTEGER NOT NULL,
          UNIQUE (business_code, valid_from)
        );
        """
    )
    connection.executemany(
        """
        INSERT INTO dimension(business_code, attribute, metric, valid_from, valid_to, is_current, is_deleted)
        VALUES (?, ?, ?, '2025-01-01T00:00:00Z', NULL, 1, 0)
        """,
        [(f"D{i:06d}", f"old-{i}", i / 10.0) for i in range(existing_rows)],
    )
    connection.commit()
    connection.close()


def database_payload(path: Path, **fields: object) -> dict[str, object]:
    payload: dict[str, object] = {
        "databaseBase64": base64.b64encode(path.read_bytes()).decode("ascii")
    }
    payload.update(fields)
    return payload


class ConnectorHarness:
    def __init__(self, dotnet: str, runner: Path, workspace: Path) -> None:
        self.dotnet = dotnet
        self.runner = runner
        self.workspace = workspace
        self.counter = 0

    def invoke(self, operation: str, payload: dict[str, object], success: bool = True) -> tuple[dict, int]:
        self.counter += 1
        request = self.workspace / f"request-{self.counter}.json"
        response = self.workspace / f"response-{self.counter}.json"
        request.write_text(json.dumps(payload, separators=(",", ":")), encoding="utf-8")
        command = [self.dotnet, str(self.runner), operation, str(request), str(response)]
        started = time.perf_counter()
        completed = subprocess.run(command, text=True, capture_output=True)
        elapsed_ms = int((time.perf_counter() - started) * 1000)
        if not response.exists():
            raise TestFailure(
                f"{operation} produced no response (exit {completed.returncode}): {completed.stderr}"
            )
        body = json.loads(response.read_text(encoding="utf-8"))
        if success and completed.returncode != 0:
            raise TestFailure(f"{operation} failed: {json.dumps(body, indent=2)}\n{completed.stderr}")
        if not success and completed.returncode == 0:
            raise TestFailure(f"{operation} unexpectedly succeeded")
        print(f"PASS invoke {operation:<18} {elapsed_ms:>6} ms")
        return body, elapsed_ms


def emitted_database(body: dict, path: Path) -> Path:
    require(body.get("integrity", {}).get("ok") is True, "connector round-trip validation failed")
    path.write_bytes(base64.b64decode(body["databaseBase64"], validate=True))
    connection = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
    integrity = connection.execute("PRAGMA integrity_check").fetchall()
    require(integrity == [("ok",)], f"SQLite integrity_check failed: {integrity[:10]}")
    foreign_key_errors = connection.execute("PRAGMA foreign_key_check").fetchall()
    require(foreign_key_errors == [], f"SQLite foreign_key_check failed: {foreign_key_errors[:10]}")
    connection.close()
    print(f"PASS sqlite integrity       {path.stat().st_size:>6} bytes")
    return path


def query_rows(harness: ConnectorHarness, path: Path, sql: str, parameters: dict | None = None) -> list[list]:
    body, _ = harness.invoke(
        "QuerySqlite",
        database_payload(
            path,
            sql=sql,
            parameters=parameters or {},
            outputMode="RowsAndSchema",
            engineOptions={"maxOutputRows": 100000},
        ),
    )
    return body["result"]["rows"]


def test_inspect_and_join(harness: ConnectorHarness, source: Path) -> None:
    body, _ = harness.invoke("InspectSqlite", database_payload(source))
    require(body["integrity"]["ok"] is True, f"inspect integrity failed: {body['integrity']}")
    require(body["database"]["pageSize"] == 512, "512-byte page size was not detected")
    require(body["database"]["userVersion"] == 7, "user_version was not read")

    sql = """
      SELECT c.region, COUNT(*) AS sale_count, SUM(s.quantity) AS units
      FROM sales AS s
      INNER JOIN customers AS c ON s.customer_id = c.id
      WHERE s.quantity >= @minimum
      GROUP BY c.region
      ORDER BY c.region
    """
    actual = query_rows(harness, source, sql, {"minimum": 3})
    connection = sqlite3.connect(source)
    expected = [list(row) for row in connection.execute(sql.replace("@minimum", "?"), (3,))]
    connection.close()
    require(actual == expected, f"join/aggregate mismatch\nactual={actual}\nexpected={expected}")
    print("PASS join + aggregate + parameter")


def test_query_features(harness: ConnectorHarness, source: Path) -> None:
    connection = sqlite3.connect(source)
    three_way_sql = """
      SELECT c.region, p.sku, SUM(s.quantity) AS units
      FROM sales s
      JOIN customers c ON s.customer_id = c.id
      JOIN products p ON s.product_id = p.id
      WHERE p.id <= 4
      GROUP BY c.region, p.sku
      HAVING SUM(s.quantity) > 0
      ORDER BY c.region, p.sku
    """
    expected = [list(row) for row in connection.execute(three_way_sql)]
    actual = query_rows(harness, source, three_way_sql)
    require(actual == expected, "three-table join/group/having result mismatch")

    cte_sql = """
      WITH recent AS (
        SELECT customer_id, SUM(quantity) AS units
        FROM sales
        WHERE sale_date >= @cutoff
        GROUP BY customer_id
      )
      SELECT c.code, r.units
      FROM recent r JOIN customers c ON r.customer_id = c.id
      WHERE r.units > 20
      ORDER BY c.code
      LIMIT 15
    """
    cutoff = "2026-06-01"
    expected = [list(row) for row in connection.execute(cte_sql.replace("@cutoff", "?"), (cutoff,))]
    actual = query_rows(harness, source, cte_sql, {"cutoff": cutoff})
    require(actual == expected, "CTE result mismatch")

    window_sql = """
      SELECT id, region,
             ROW_NUMBER() OVER (PARTITION BY region ORDER BY score DESC) AS rn
      FROM customers
      ORDER BY region, id
      LIMIT 20
    """
    expected = [list(row) for row in connection.execute(window_sql)]
    actual = query_rows(harness, source, window_sql)
    require(actual == expected, "window function result mismatch")

    response, _ = harness.invoke(
        "QuerySqlite",
        database_payload(
            source,
            sql="SELECT payload FROM products WHERE id=1",
            outputMode="ObjectArray",
        ),
    )
    expected_blob = base64.b64encode(connection.execute("SELECT payload FROM products WHERE id=1").fetchone()[0]).decode("ascii")
    require(response["result"]["rows"] == [{"payload": expected_blob}], "BLOB JSON encoding mismatch")

    injected = query_rows(
        harness,
        source,
        "SELECT code FROM customers WHERE name=@name",
        {"name": "Customer 1' OR 1=1 --"},
    )
    require(injected == [], "SQL parameter quoting allowed text to become executable SQL")
    connection.close()
    print("PASS joins + CTE + window + BLOB + safe parameters")


def test_sql_mutation(harness: ConnectorHarness, source: Path, workspace: Path) -> Path:
    sql = """
      BEGIN;
      UPDATE customers SET name = UPPER(name), score = score + 0.5 WHERE id <= 3;
      INSERT INTO customers(id, code, name, region, score)
        VALUES (1001, 'C01001', 'New customer', 'North', 9.5);
      DELETE FROM sales WHERE id = 1;
      INSERT INTO events(code) VALUES ('EVT-NEW');
      PRAGMA user_version = 42;
      COMMIT;
    """
    body, _ = harness.invoke("ExecuteSqlite", database_payload(source, sql=sql))
    require(body["changes"]["updated"] >= 4, "SQL update count did not include data and PRAGMA changes")
    output = emitted_database(body, workspace / "sql-mutated.db")
    connection = sqlite3.connect(output)
    require(connection.execute("PRAGMA user_version").fetchone()[0] == 42, "user_version write failed")
    require(connection.execute("SELECT COUNT(*) FROM sales WHERE id=1").fetchone()[0] == 0, "DELETE failed")
    require(connection.execute("SELECT name FROM customers WHERE id=1").fetchone()[0] == "CUSTOMER 1", "UPDATE failed")
    require(connection.execute("SELECT code FROM customers WHERE id=1001").fetchone()[0] == "C01001", "INSERT failed")
    require(connection.execute("SELECT id, value FROM events WHERE code='EVT-NEW'").fetchone() == (51, "seed"), "AUTOINCREMENT sequence/default handling failed")
    connection.close()
    print("PASS SQL mutation semantics")
    return output


def test_bulk_upsert(harness: ConnectorHarness, source: Path, workspace: Path) -> Path:
    body, _ = harness.invoke(
        "BulkUpsertSqlite",
        database_payload(
            source,
            table="customers",
            format="Json",
            jsonRows=[
                {"code": "c00001", "name": "Bulk changed", "region": "West", "score": 77.0},
                {"code": "C09999", "name": "Bulk inserted", "region": "East", "score": 88.0},
            ],
            keyColumns=["code"],
            updateColumns=["name", "region", "score"],
            mode="Upsert",
            nullUpdateBehavior="OverwriteNulls",
        ),
    )
    require(body["changes"]["updated"] == 1 and body["changes"]["inserted"] == 1, "JSON bulk counts are wrong")
    json_output = emitted_database(body, workspace / "bulk-json.db")

    csv_text = 'code,name,region,score\nC00002,"CSV, changed",South,55.5\nC08888,"Line one\nLine two",North,66.5\n'
    body, _ = harness.invoke(
        "BulkUpsertSqlite",
        database_payload(
            json_output,
            table="customers",
            format="Csv",
            csvText=csv_text,
            keyColumns=["code"],
            updateColumns=["name", "region", "score"],
            mode="Upsert",
        ),
    )
    output = emitted_database(body, workspace / "bulk-csv.db")
    connection = sqlite3.connect(output)
    require(connection.execute("SELECT name FROM customers WHERE code='C00001'").fetchone()[0] == "Bulk changed", "NOCASE key upsert failed")
    require(connection.execute("SELECT name FROM customers WHERE code='C00002'").fetchone()[0] == "CSV, changed", "CSV quoted value failed")
    require(connection.execute("SELECT name FROM customers WHERE code='C08888'").fetchone()[0] == "Line one\nLine two", "CSV embedded newline failed")
    connection.close()
    print("PASS JSON + CSV O(n) upsert")
    return output


def test_scd2(harness: ConnectorHarness, source: Path, workspace: Path) -> Path:
    body, _ = harness.invoke(
        "ApplyScd2Sqlite",
        database_payload(
            source,
            table="dim_product",
            format="Json",
            jsonRows=[
                {"sku": "SKU0001", "name": "Widget v2", "price": 12.5},
                {"sku": "SKU0003", "name": "New item", "price": 30.0},
            ],
            businessKeyColumns=["sku"],
            trackedColumns=["name", "price"],
            snapshotDate="2026-01-01T00:00:00Z",
            absenceMeansDeletion=True,
            deletionMode="Tombstone",
            validFromColumn="valid_from",
            validToColumn="valid_to",
            isCurrentColumn="is_current",
            isDeletedColumn="is_deleted",
        ),
    )
    require(body["changes"]["closedVersions"] == 2, "SCD2 did not close changed and absent versions")
    require(body["changes"]["tombstones"] == 1, "SCD2 tombstone count is wrong")
    output = emitted_database(body, workspace / "scd2.db")
    connection = sqlite3.connect(output)
    rows = connection.execute(
        "SELECT sku, name, price, valid_from, valid_to, is_current, is_deleted FROM dim_product ORDER BY sku, valid_from"
    ).fetchall()
    require(len(rows) == 5, f"SCD2 expected 5 rows, got {len(rows)}: {rows}")
    current = connection.execute(
        "SELECT sku, name, is_deleted FROM dim_product WHERE is_current=1 ORDER BY sku"
    ).fetchall()
    require(current == [("SKU0001", "Widget v2", 0), ("SKU0002", "Gadget", 1), ("SKU0003", "New item", 0)], f"SCD2 current rows are wrong: {current}")
    overlaps = connection.execute(
        """
        SELECT COUNT(*) FROM dim_product a JOIN dim_product b
          ON a.sku=b.sku AND a.product_key < b.product_key
         AND COALESCE(a.valid_to, '9999') > b.valid_from
         AND COALESCE(b.valid_to, '9999') > a.valid_from
        """
    ).fetchone()[0]
    require(overlaps == 0, "SCD2 generated overlapping intervals")
    connection.close()

    repeat_body, _ = harness.invoke(
        "ApplyScd2Sqlite",
        database_payload(
            output,
            table="dim_product",
            format="Json",
            jsonRows=[
                {"sku": "SKU0001", "name": "Widget v2", "price": 12.5},
                {"sku": "SKU0003", "name": "New item", "price": 30.0},
            ],
            businessKeyColumns=["sku"],
            trackedColumns=["name", "price"],
            snapshotDate="2026-01-01T00:00:00Z",
            absenceMeansDeletion=True,
            deletionMode="Tombstone",
        ),
    )
    require(repeat_body["changes"]["totalChanged"] == 0, "repeated SCD2 snapshot was not idempotent")
    repeat_output = emitted_database(repeat_body, workspace / "scd2-repeat.db")

    correction_body, _ = harness.invoke(
        "ApplyScd2Sqlite",
        database_payload(
            repeat_output,
            table="dim_product",
            format="Json",
            jsonRows=[
                {"sku": "SKU0001", "name": "Widget corrected", "price": 13.0},
                {"sku": "SKU0003", "name": "New item", "price": 30.0},
            ],
            businessKeyColumns=["sku"],
            trackedColumns=["name", "price"],
            snapshotDate="2026-01-01T00:00:00Z",
            absenceMeansDeletion=True,
            deletionMode="Tombstone",
        ),
    )
    require(correction_body["changes"]["updated"] == 1 and correction_body["changes"]["inserted"] == 0, "same-effective-date correction did not update in place")
    correction_output = emitted_database(correction_body, workspace / "scd2-correction.db")
    connection = sqlite3.connect(correction_output)
    require(connection.execute("SELECT COUNT(*) FROM dim_product").fetchone()[0] == 5, "same-date correction added history")
    require(connection.execute("SELECT name FROM dim_product WHERE sku='SKU0001' AND is_current=1").fetchone()[0] == "Widget corrected", "same-date correction value is wrong")
    connection.close()

    rejected, _ = harness.invoke(
        "ApplyScd2Sqlite",
        database_payload(
            correction_output,
            table="dim_product",
            format="Json",
            jsonRows=[{"sku": "SKU0001", "name": "Out of order", "price": 1.0}],
            businessKeyColumns=["sku"],
            trackedColumns=["name", "price"],
            snapshotDate="2025-12-31T00:00:00Z",
        ),
        success=False,
    )
    require(rejected.get("error", {}).get("code") == "OUT_OF_ORDER_SNAPSHOT", "out-of-order SCD2 snapshot was not rejected")
    print("PASS stateful SCD Type 2")
    return correction_output


def test_file_format_variants(harness: ConnectorHarness, workspace: Path) -> None:
    for page_size, encoding in ((4096, "UTF-16le"), (65536, "UTF-16be")):
        source = workspace / f"variant-{page_size}-{encoding}.db"
        make_variant_database(source, page_size, encoding)
        connection = sqlite3.connect(source)
        expected = connection.execute(
            "SELECT id, label, typeof(amount), amount, hex(payload) FROM edge_values ORDER BY id"
        ).fetchall()
        connection.close()
        body, _ = harness.invoke(
            "ExecuteSqlite",
            database_payload(source, sql="UPDATE edge_values SET label = label WHERE id = -5; PRAGMA user_version = 19"),
        )
        output = emitted_database(body, workspace / f"variant-output-{page_size}-{encoding}.db")
        connection = sqlite3.connect(output)
        actual = connection.execute(
            "SELECT id, label, typeof(amount), amount, hex(payload) FROM edge_values ORDER BY id"
        ).fetchall()
        actual_page_size = connection.execute("PRAGMA page_size").fetchone()[0]
        actual_encoding = connection.execute("PRAGMA encoding").fetchone()[0].replace("-", "").lower()
        connection.close()
        require(actual == expected, f"logical values changed for {page_size}/{encoding}")
        require(actual_page_size == page_size, f"page size {page_size} was not preserved")
        require(actual_encoding == encoding.replace("-", "").lower(), f"encoding {encoding} was not preserved: {actual_encoding}")
    print("PASS page sizes + UTF-16 + edge values")


def test_wal_guard(harness: ConnectorHarness, source: Path, workspace: Path) -> None:
    wal = workspace / "wal-main.db"
    wal.write_bytes(source.read_bytes())
    connection = sqlite3.connect(wal)
    require(connection.execute("PRAGMA journal_mode=WAL").fetchone()[0].lower() == "wal", "could not create WAL fixture")
    connection.execute("UPDATE customers SET score=123 WHERE id=1")
    connection.commit()
    connection.execute("PRAGMA wal_checkpoint(TRUNCATE)")
    connection.close()
    require(wal.read_bytes()[18] == 2, "WAL fixture header is not marked WAL")

    inspected, _ = harness.invoke("InspectSqlite", database_payload(wal))
    require(inspected["database"]["walMode"] is True, "WAL mode was not reported")
    rejected, _ = harness.invoke(
        "ExecuteSqlite", database_payload(wal, sql="PRAGMA user_version=88"), success=False
    )
    require(rejected.get("error", {}).get("code") == "WAL_SNAPSHOT_UNSAFE", "unsafe WAL rewrite was not rejected")
    accepted, _ = harness.invoke(
        "ExecuteSqlite",
        database_payload(
            wal,
            sql="PRAGMA user_version=88",
            limits={"allowWalSnapshot": True},
        ),
    )
    output = emitted_database(accepted, workspace / "wal-snapshot-output.db")
    require(output.read_bytes()[18] == 1, "rebuilt WAL snapshot did not return to rollback-journal format")
    print("PASS WAL snapshot safety guard")


def test_bulk_performance(harness: ConnectorHarness, workspace: Path) -> None:
    source = workspace / "bulk-performance.db"
    make_bulk_database(source)
    rows = [
        {"code": f"B{i:06d}", "metric": i * 10, "description": f"new-{i}"}
        for i in range(20000)
    ]
    body, elapsed_ms = harness.invoke(
        "BulkUpsertSqlite",
        database_payload(
            source,
            table="bulk_target",
            format="Json",
            jsonRows=rows,
            keyColumns=["code"],
            updateColumns=["metric", "description"],
            mode="Upsert",
        ),
    )
    require(body["changes"]["updated"] == 10000, "large bulk update count is wrong")
    require(body["changes"]["inserted"] == 10000, "large bulk insert count is wrong")
    require(elapsed_ms < 30000, f"20k-row bulk test exceeded 30 seconds: {elapsed_ms} ms")
    output = emitted_database(body, workspace / "bulk-performance-output.db")
    connection = sqlite3.connect(output)
    require(connection.execute("SELECT COUNT(*) FROM bulk_target").fetchone()[0] == 20000, "large bulk row count is wrong")
    require(connection.execute("SELECT metric FROM bulk_target WHERE code='B019999'").fetchone()[0] == 199990, "large bulk value is wrong")
    require(connection.execute("SELECT MAX(id) FROM bulk_target").fetchone()[0] == 20000, "bulk AUTOINCREMENT allocation is wrong")
    connection.close()
    print(f"PASS 20k-row bulk benchmark {elapsed_ms} ms")


def test_scd2_performance(harness: ConnectorHarness, workspace: Path) -> None:
    source = workspace / "scd-performance.db"
    make_scd_database(source)
    rows = []
    for i in range(8000):
        changed = i < 4000
        rows.append(
            {
                "business_code": f"D{i:06d}",
                "attribute": f"new-{i}" if changed else f"old-{i}",
                "metric": (i / 10.0) + (1.0 if changed else 0.0),
            }
        )
    body, elapsed_ms = harness.invoke(
        "ApplyScd2Sqlite",
        database_payload(
            source,
            table="dimension",
            format="Json",
            jsonRows=rows,
            businessKeyColumns=["business_code"],
            trackedColumns=["attribute", "metric"],
            snapshotDate="2026-06-30T00:00:00Z",
            absenceMeansDeletion=True,
            deletionMode="Tombstone",
            validFromColumn="valid_from",
            validToColumn="valid_to",
            isCurrentColumn="is_current",
            isDeletedColumn="is_deleted",
        ),
    )
    changes = body["changes"]
    require(changes["inserted"] == 6000, f"large SCD2 inserted count is wrong: {changes}")
    require(changes["closedVersions"] == 6000, f"large SCD2 close count is wrong: {changes}")
    require(changes["tombstones"] == 2000, f"large SCD2 tombstone count is wrong: {changes}")
    require(changes["unchanged"] == 4000, f"large SCD2 unchanged count is wrong: {changes}")
    require(elapsed_ms < 30000, f"10k-dimension SCD2 test exceeded 30 seconds: {elapsed_ms} ms")
    output = emitted_database(body, workspace / "scd-performance-output.db")
    connection = sqlite3.connect(output)
    require(connection.execute("SELECT COUNT(*) FROM dimension").fetchone()[0] == 16000, "large SCD2 total count is wrong")
    require(connection.execute("SELECT COUNT(*) FROM dimension WHERE is_current=1").fetchone()[0] == 10000, "large SCD2 current count is wrong")
    require(connection.execute("SELECT COUNT(*) FROM dimension WHERE is_current=1 AND is_deleted=1").fetchone()[0] == 2000, "large SCD2 deleted-current count is wrong")
    connection.close()
    print(f"PASS 10k-key SCD2 benchmark {elapsed_ms} ms")


def test_rejections(harness: ConnectorHarness, source: Path, workspace: Path) -> None:
    truncated = workspace / "truncated.db"
    truncated.write_bytes(source.read_bytes()[:700])
    body, _ = harness.invoke("InspectSqlite", database_payload(truncated), success=False)
    require("error" in body, "truncated input did not return a structured error")

    without_rowid = workspace / "without-rowid.db"
    connection = sqlite3.connect(without_rowid)
    connection.execute("CREATE TABLE wr(code TEXT PRIMARY KEY, value TEXT) WITHOUT ROWID")
    connection.commit()
    connection.close()
    body, _ = harness.invoke("InspectSqlite", database_payload(without_rowid), success=False)
    require(body.get("error", {}).get("code") == "WITHOUT_ROWID_UNSUPPORTED", "WITHOUT ROWID rejection code is wrong")

    guarded = workspace / "guarded-schema.db"
    connection = sqlite3.connect(guarded)
    connection.executescript(
        """
        CREATE TABLE checked_value(id INTEGER PRIMARY KEY, value INTEGER CHECK(value > 0));
        INSERT INTO checked_value VALUES(1, 1);
        CREATE TABLE audit(id INTEGER PRIMARY KEY, message TEXT);
        CREATE TRIGGER checked_audit AFTER UPDATE ON checked_value
        BEGIN INSERT INTO audit(message) VALUES('updated'); END;
        """
    )
    connection.commit()
    connection.close()
    body, _ = harness.invoke(
        "ExecuteSqlite",
        database_payload(guarded, sql="UPDATE checked_value SET value=2 WHERE id=1"),
        success=False,
    )
    require(body.get("error", {}).get("code") in {"TRIGGERS_UNSUPPORTED", "CHECK_CONSTRAINTS_UNSUPPORTED"}, "guarded schema write was not rejected")

    body, _ = harness.invoke(
        "ExecuteSqlite",
        database_payload(source, sql="CREATE TABLE forbidden(x INTEGER)"),
        success=False,
    )
    require(body.get("error", {}).get("code") == "UNSUPPORTED_SQL_STATEMENT", "unsupported DDL error code is wrong")

    body, _ = harness.invoke(
        "ExecuteSqlite",
        database_payload(source, sql="DELETE FROM customers WHERE id=1"),
        success=False,
    )
    require(body.get("error", {}).get("code") == "CONSTRAINT_VALIDATION_FAILED", "foreign-key violation was not rejected")
    print("PASS structured rejection paths")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dotnet", default=os.environ.get("DOTNET", "dotnet"))
    parser.add_argument("--runner", type=Path, default=DEFAULT_RUNNER)
    args = parser.parse_args()
    if not args.runner.exists():
        raise TestFailure(f"Runner not found: {args.runner}. Build SqliteDatabaseRunner first.")

    test_connector_assets()

    with tempfile.TemporaryDirectory(prefix="pa-sqlite-tests-") as directory:
        workspace = Path(directory)
        source = workspace / "core.db"
        make_core_database(source)
        harness = ConnectorHarness(args.dotnet, args.runner.resolve(), workspace)
        started = time.perf_counter()
        test_inspect_and_join(harness, source)
        test_query_features(harness, source)
        sql_output = test_sql_mutation(harness, source, workspace)
        bulk_output = test_bulk_upsert(harness, sql_output, workspace)
        test_scd2(harness, bulk_output, workspace)
        test_file_format_variants(harness, workspace)
        test_wal_guard(harness, source, workspace)
        test_bulk_performance(harness, workspace)
        test_scd2_performance(harness, workspace)
        test_rejections(harness, source, workspace)
        elapsed = time.perf_counter() - started
        print(f"PASS all connector tests in {elapsed:.2f} s")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except TestFailure as error:
        print(f"FAIL {error}", file=sys.stderr)
        raise SystemExit(1)
