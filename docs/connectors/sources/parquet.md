# Parquet source

The `file` source with `Format.Type = 'parquet'` reads the embedded Parquet schema and loads one row group at a time. The source rejects any row group exceeding `Format.Options.MaxSourceRowGroupRows`; records emitted to the migration graph must serialize as JSON objects. Use a `DirectoryInfo` connection with a relative `Path`, or a `FileInfo` connection to name the exact input file and omit `Path`.

## Configuration example

```powershell
Source = @{
    Type = 'file'
    Format = @{
        Type = 'parquet'
        Options = @{
            Columns = @('SourceId', 'DataverseId', 'Name')
            MaxSourceRowGroupRows = 100000
            TreatByteArrayAsString = $false
        }
    }
    ConnectionId = 'input-files'
    Path = 'customers.parquet'
    Key = 'SourceId'
    Id = 'DataverseId'
}
```

## Format options

Options are case-insensitive when deserialized. Every option is listed; `SchemaByStep` applies to target writing and `SourceSchemaByStep` to fixture conversion, not to runtime source schema selection.

| Option | Default | Source behavior |
|---|---|---|
| `CompressionMethod` | `Snappy` | Compression setting used by Parquet.Net; source decoding is based on the file. |
| `CompressionLevel` | `Optimal` | Compression level used when writing; no source effect. |
| `TargetRowGroupSize` | `100000` | Writer row-group size; no source effect. |
| `MaxSourceRowGroupRows` | `250000` | Maximum accepted row group size. An oversized group fails. |
| `DictionaryEncodingThreshold` | `0.8` | Writer dictionary-encoding threshold (`0..1`); no source effect. |
| `DictionaryEncodingSampleSize` | `1000` | Writer dictionary-encoding sample size; no source effect. |
| `TreatByteArrayAsString` | `false` | Parquet.Net byte-array interpretation. |
| `TreatBigIntegersAsDates` | `false` | Parquet.Net large-integer interpretation. |
| `UseDateOnlyTypeForDates` | `true` | Read dates using Parquet.Net `DateOnly` behavior. |
| `Columns` | `null` | Projection; omitted means all data fields. |
| `BinaryColumns` | `[]` | String fields containing relative sidecar file references; each must be selected by `Columns`. |
| `SchemaByStep` | `null` | Target-only explicit output schemas, keyed by step ID. |
| `SourceSchemaByStep` | `null` | Fixture-conversion schema; runtime reading uses embedded file schema. |

The source emits JSON-compatible objects and converts supported primitive and date values. Use `Columns` to limit the projected fields and reduce row size.

| Parquet logical value | Type in the PowerShell mapping |
|---|---|
| `string` | `System.String` |
| `json` | `System.String` containing the JSON text |
| `bool`, integer widths, `float`, `double`, `decimal` | `Boolean`, `Int64` for integers, and `Double` for fractional JSON numbers |
| `guid` | `System.String` |
| `date`, `datetime` | `System.DateTime` for ISO-formatted values after default PowerShell JSON parsing |
| `binary` | Base64 `System.String` after JSON transport |
| `BinaryColumns` reference | `System.String`, then `MigrationBinaryValue` in the source mapping |

Sidecar reference fields must have a Parquet string schema. See [binary values and sidecar files](../../concepts/binary-values-and-sidecars.md).
