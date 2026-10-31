# Binary values and sidecar files

Migration records are mapped as PowerShell objects, but connectors expose a consistent set of logical values. A connector may preserve provider-native CLR types or serialize values through JSON; its connector guide documents the concrete representations.

| Logical value | Common .NET representation | Notes |
|---|---|---|
| Null | `$null` | A missing field and an explicit null are connector-specific. |
| Boolean | `System.Boolean` | JSON booleans remain booleans. |
| Integer | `System.Byte`, `Int16`, `Int32`, or `Int64` | The width depends on the source schema or provider. |
| Decimal / approximate number | `System.Decimal`, `Single`, or `Double` | Preserve decimal values for money and exact quantities. |
| Text | `System.String` | Includes identifiers that are not declared as GUIDs. |
| Identifier | `System.Guid` or `System.String` | JSON/API sources commonly retain GUIDs as strings; typed file and database connectors can emit `Guid`. |
| Date / date-time | `System.DateOnly`, `DateTime`, or `DateTimeOffset` | JSON and REST interfaces commonly represent these as ISO strings. |
| Object / array | `IDictionary`/PowerShell hashtable and `IList`/array, or `JsonElement` | Nested JSON is connector-specific; map it to a target-supported shape explicitly. |
| Binary content | `MigrationBinaryValue` | File-backed, streamable value used by binary-capable connectors. Some connectors also accept `byte[]`, streams, or Base64 strings at their boundary. |

JSON transport can normalize CLR widths and representations: integer JSON values normally arrive as `Int64`, fractional values as `Double`, GUIDs as strings, and ISO-looking date/time strings may be parsed as `DateTime` by PowerShell's default `ConvertFrom-Json` behavior. Use explicit conversions in mappings when a target requires a specific CLR type or when preserving an input timestamp's original text/offset matters.

`MigrationBinaryValue` keeps the bytes in run-scoped disk staging. Its transport descriptor, not its content, crosses persisted migration batches. A mapping can pass the value unchanged, replace it, or use its stream and transformation helpers. See [`New-MigrationBinaryValue`](../cmdlets/new-migration-binary-value.md) for Base64 encode/decode, stream-backed JSON serialization, MIME detection, and creating replacement values.

## Sidecar files for structured outputs

CSV, JSON Lines, and Parquet targets can store binary fields as separate files instead of embedding large values in the structured output. The target writes a sibling directory named `<target-file>.attachments/` and puts a relative path in the corresponding record column. No manifest is written: the reference column and files are the complete interchange format.

Binary fields are detected automatically when a top-level mapped value is a `MigrationBinaryValue`, `byte[]`, or readable `Stream`. Set `Target.Format.Options.BinaryColumns` to restrict and explicitly declare those columns. For CSV, each declared field must be in `Format.Options.ColumnsByStep.<stepId>`; for Parquet, its `Format.Options.SchemaByStep.<stepId>` type must be `string` or `json`. JSON Lines has no schema.

The default sidecar name preserves a safe source filename and extension (or infers one from MIME type), under run/record/column directories to avoid collisions. A generated CSV/JSONL/Parquet column value looks like:

```text
records.jsonl.attachments/<run-id-hash>/<sequence>/<record>/<column>/invoice.pdf
```

The path is relative to the structured file's parent directory. Copy or move the data file and its `.attachments` directory together, keeping their names unchanged so the references remain valid. Because there is no manifest, original MIME type and other metadata are not guaranteed to round-trip; the source detects MIME type from the file and its name.

On a source, set `Source.Format.Options.BinaryColumns` to the path-valued fields. The source resolves each path relative to the source file's parent directory and replaces it with a `MigrationBinaryValue`. The field must be included in `Format.Options.Columns` when that option projects CSV or Parquet fields. For CSV, `BinaryColumns` keeps the reference as text even if a `ColumnTypes` conversion is configured; Parquet reference fields must have a string schema.

```powershell
Source = @{
    Type = 'file'
    Format = @{
        Type = 'jsonl'
        Options = @{ BinaryColumns = @('Content') }
    }
    ConnectionId = 'files'
    Path = 'records.jsonl'
    Key = 'Id'
}

Target = @{
    Type = 'file'
    Format = @{
        Type = 'jsonl'
        Options = @{ BinaryColumns = @('Content') }
    }
    ConnectionId = 'output'
    Path = 'records.jsonl'
}
```

By default, the source resolver accepts only relative paths that remain under the structured source file's directory. Optional `Source.ResolveBinaryReference` and `Target.CreateBinaryReference` scriptblocks customize reference resolution and naming:

```powershell
Source = @{
    Type = 'file'
    Format = @{
        Type = 'csv'
        Options = @{
            BinaryColumns = @('Content')
            Columns = @('Id', 'Content')
        }
    }
    ConnectionId = 'files'
    Path = 'records.csv'
    Key = 'Id'
    ResolveBinaryReference = {
        param($Reference, $Row, $ColumnName, $Context)
        [System.IO.Path]::Combine(
            [System.IO.Path]::GetDirectoryName($Context.SourcePath),
            $Reference)
    }
}

Target = @{
    Type = 'file'
    Format = @{
        Type = 'csv'
        Options = @{
            BinaryColumns = @('Content')
            ColumnsByStep = @{ Records = @('Id', 'Content') }
        }
    }
    ConnectionId = 'output'
    Path = 'records.csv'
    CreateBinaryReference = {
        param($Row, $ColumnName, $Value, $DefaultRelativePath, $Context)
        $DefaultRelativePath
    }
}
```

`ResolveBinaryReference` receives `($reference, $row, $columnName, $context)` and must emit exactly one `MigrationBinaryValue`, readable stream, `byte[]`, `FileInfo`, or path. Its context contains `StepId`, `RunId`, `ColumnName`, `SourcePath`, and `StagingRoot`. `CreateBinaryReference` receives `($row, $columnName, $value, $defaultRelativePath, $context)` and must emit exactly one relative path; its context contains `StepId`, `RunId`, `Sequence`, `RecordIndex`, `TargetPath`, and `StagingRoot`. The engine rejects rooted paths, `.`/`..` segments, and paths that escape the sidecar root.

Sidecar files are streamed from staging and are written before the structured file is atomically published. If finalization fails, the previous structured file remains intact, but newly written sidecars can remain unreferenced. Reusing a path for different bytes fails; use distinct names when customizing references. A later run uses a distinct run-specific sidecar directory; remove old directories only after checking that no structured output references them.

## Connector-specific values

The shared logical types above are not a promise that every connector accepts every value. See the source and target connector guides for the concrete emitted and accepted type tables. Particularly important differences include:

- CSV defaults to text and uses `Format.Options.ColumnTypes` to produce typed CLR values; a `Format.Options.BinaryColumns` path remains text until the source resolves it.
- JSON Lines follows JSON scalar, object, array, and null semantics. JSON itself has no date, GUID, or binary type.
- Parquet requires an explicit output schema and preserves its declared primitive types; its `binary` fields use Base64 strings, while sidecar references use `string` or `json`.
- Relational connectors map values according to provider and SQL schema. File-backed `MigrationBinaryValue` support is connector-specific and is not equivalent to a SQL `BLOB`/`BYTEA` value.
- Dataverse File/Image columns and document connectors use `MigrationBinaryValue`; Notes/activity attachment bodies may require Base64 at the upstream API boundary.
