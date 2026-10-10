# SQLite connector end-to-end tests

`run_tests.py` uses only the Python standard library. It generates real SQLite databases with Python's `sqlite3`, invokes the connector through `SqliteDatabaseRunner`, and then reopens every emitted database with SQLite itself.

The suite fails unless both of these native checks are clean for every write result:

```sql
PRAGMA integrity_check;
PRAGMA foreign_key_check;
```

It also compares query and mutation results, validates all connector assets and operation IDs, checks the 1 MB script/namespace constraints, and runs 20,000-row bulk-upsert and 10,000-key SCD2 regression benchmarks.

Run from the repository root:

```powershell
dotnet build testing/SqliteDatabaseCompile/SqliteDatabaseCompile.csproj
dotnet build testing/SqliteDatabaseRunner/SqliteDatabaseRunner.csproj
python testing/SqliteDatabaseTests/run_tests.py
```

Use `--dotnet <path>` and `--runner <path>` when the runtime or compiled runner is not on the default path.
