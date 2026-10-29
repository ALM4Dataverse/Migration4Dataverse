# Parquet source

The `parquet` source reads the embedded Parquet schema and loads one row group at a time. The source rejects any row group exceeding `MaxSourceRowGroupRows`; records emitted to the migration graph must serialize as JSON objects. Use a `DirectoryInfo` connection with a relative `Path`, or a `FileInfo` connection to name the exact input file and omit `Path`.

## Configuration example

```powershell
Source = @{
    Type = 'parquet'
    ConnectionId = 'input-files'
    Path = 'customers.parquet'
    Key = 'SourceId'
    Id = 'DataverseId'
    Options = @{
        Columns = @('SourceId', 'DataverseId', 'Name')
        MaxSourceRowGroupRows = 100000
        TreatByteArrayAsString = $false
    }
}
```

## Options

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
| `SchemaByStep` | `null` | Target-only explicit output schemas, keyed by step ID. |
| `SourceSchemaByStep` | `null` | Fixture-conversion schema; runtime reading uses embedded file schema. |

The source emits JSON-compatible objects and converts supported primitive and date values. Use `Columns` to limit the projected fields and reduce row size.
