# Source connectors

Source steps can read from these built-in connectors:

| Type | What it reads | Reference |
|---|---|---|
| `jsonl` | UTF-8 JSON Lines objects | [JSON Lines](./jsonl.md) |
| `csv` | CSV records with configurable parsing and conversion | [CSV](./csv.md) |
| `parquet` | Parquet rows, one bounded row group at a time | [Parquet](./parquet.md) |
| `sqlite` | Query results streamed from a SQLite database | [SQLite](./sqlite.md) |
| `duckdb` | Query results streamed from a DuckDB database | [DuckDB](./duckdb.md) |
| `dataverse` | Dataverse rows via Simple, FetchXML, or Sql4Cds queries | [Dataverse](./dataverse.md) |
| `custom` | Paged PowerShell callback results | [Custom](./custom.md) |

Every source also uses the common `ConnectionId`, `Path` (file connectors), `Key`, `Id`, and optional `ExpectedRecordCount` properties documented in [step configuration](../../configuration.md#source-properties).

File connectors use a `System.IO.DirectoryInfo` connection and resolve the source `Path` below that root. SQLite and DuckDB use named ADO.NET connections and a SQL query in `Options.Query`. Source records must be JSON objects/dictionaries. `Key` identifies records for stable source ordering and resume; `Id` names the source value converted to the target `Id` GUID.
