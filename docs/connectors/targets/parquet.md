# Parquet target

The `file` target with `Format.Type = 'parquet'` writes the complete step output to one file and requires an explicit `Format.Options.SchemaByStep` entry for each target step. Batches are staged separately and assembled into ordered Parquet row groups during the scheduler's completion phase. Replacement is atomic per destination; multiple file targets are not a cross-file transaction. A row group's target size is controlled by `Format.Options.TargetRowGroupSize`.

## Configuration example

```powershell
Target = @{
    Type = 'file'
    Format = @{
        Type = 'parquet'
        Options = @{
            SchemaByStep = @{
                Customers = @(
                    @{ Name = 'Id'; Type = 'guid'; Nullable = $false }
                    @{ Name = 'name'; Type = 'string'; Nullable = $true }
                    @{ Name = 'createdon'; Type = 'datetime'; DateTimeUnit = 'Micros' }
                )
            }
            CompressionMethod = 'Snappy'
        }
    }
    ConnectionId = 'output-files'
    Path = 'customers.parquet'
    Operation = 'Write'
}
```

## Format options

Options are case-insensitive when deserialized.

| Option | Default | Target behavior |
|---|---|---|
| `CompressionMethod` | `Snappy` | Parquet.Net compression method enum name. |
| `CompressionLevel` | `Optimal` | .NET compression-level enum name. |
| `TargetRowGroupSize` | `100000` | Positive rows per output row group in the completed file. |
| `MaxSourceRowGroupRows` | `250000` | Source-only maximum accepted row-group size. |
| `DictionaryEncodingThreshold` | `0.8` | Dictionary encoding threshold, from `0` through `1`. |
| `DictionaryEncodingSampleSize` | `1000` | Non-negative number of values sampled for dictionary encoding. |
| `TreatByteArrayAsString` | `false` | Parquet.Net byte-array interpretation. |
| `TreatBigIntegersAsDates` | `false` | Parquet.Net large-integer interpretation. |
| `UseDateOnlyTypeForDates` | `true` | Use Parquet.Net `DateOnly` behavior for dates. |
| `Columns` | `null` | Source projection; not a target schema. |
| `BinaryColumns` | `[]` (auto-detect) | When set, restrict sidecar output to these top-level binary fields; schema fields must be `string` or `json`. |
| `SchemaByStep` | `null` | Required output schemas keyed by step ID. |
| `SourceSchemaByStep` | `null` | Fixture-conversion helper; not used by runtime target writing. |

`ConnectionId` must resolve to a `DirectoryInfo` or `FileInfo`. With a directory, `Path` names the exact output file relative to that directory; with a `FileInfo`, omit `Path`. Each `Format.Options.SchemaByStep` field has these properties:

| Property | Default | Description |
|---|---|---|
| `Name` | Required | Exact output field name. Names must be unique within a target step schema. |
| `Type` | `string` | `string`, `json`, `bool`/`boolean`, `byte`, `short`/`int16`, `int`/`int32`, `long`/`int64`, `float`/`single`, `double`, `decimal`, `guid`, `date`, `datetime`, or `binary`. |
| `Nullable` | `true` | Whether null values are allowed. A non-nullable field receiving null fails target finalization and the run. |
| `Precision` | `29` | Decimal precision, 1 through 29; used for `decimal`. |
| `Scale` | `10` | Decimal scale from 0 through `Precision`; used for `decimal`. |
| `DateTimeFormat` | `Timestamp` | Parquet.Net `DateTimeFormat` enum name; used for `datetime`. |
| `DateTimeUnit` | `Micros` | Parquet.Net `DateTimeTimeUnit` enum name; used for `datetime`. |
| `IsAdjustedToUtc` | `true` | UTC adjustment metadata for `datetime`. |

JSON date values must be ISO date/time strings for `date` and `datetime`; `binary` values must be base64 strings. `json` fields are serialized to text. Unsupported types, invalid schema properties, and values that cannot be converted fail instead of being silently coerced.

| Parquet field type | Accepted PowerShell/JSON value |
|---|---|
| `string`, `json` | String; object/array values for `json` are serialized as JSON text |
| `bool`, integer, floating-point, `decimal` | Boolean or JSON number convertible to the declared type |
| `guid`, `date`, `datetime` | String in GUID, ISO date, or ISO date/time form |
| `binary` | Base64 `System.String`, decoded to Parquet bytes |
| Sidecar reference (`BinaryColumns`) | `MigrationBinaryValue`, `byte[]`, or readable `Stream`; emits a path in a `string`/`json` field |

See [binary values and sidecar files](../../concepts/binary-values-and-sidecars.md) for rehydrating sidecars from Parquet sources.
