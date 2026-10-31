# CSV target

The `file` target with `Format.Type = 'csv'` writes the complete step output to one CSV file. Batches are staged separately and combined in sequence after the migration succeeds, with at most one header row. Configure `Format.Options.ColumnsByStep` to define the output columns and their order for every CSV target step. Format option names are case-insensitive when deserialized.

## Configuration example

```powershell
Target = @{
    Type = 'file'
    Format = @{
        Type = 'csv'
        Options = @{
            ColumnsByStep = @{
                Customers = @('Id', 'name', 'category')
            }
            WriteHeaderRecord = $true
            NullValue = ''
            QuoteAllFields = $false
        }
    }
    ConnectionId = 'output-files'
    Path = 'customers.csv'
    Operation = 'Write'
}
```

## Format options

Defaults are shown below. The source-reader `ColumnTypes` option accepts `string` (default), `bool`/`boolean`, `byte`, `short`/`int16`, `int`/`int32`, `long`/`int64`, `float`/`single`, `double`, `decimal`, `guid`, `date`, `datetime`, `datetimeoffset`, `json`, and `binary`.

| Option | Default | Target behavior |
|---|---|---|
| `Delimiter` | `,` | Field separator; must be non-empty. |
| `Quote` | `"` | One-character quote delimiter. |
| `Escape` | `"` | One-character escape character. |
| `Encoding` | `utf-8` | .NET encoding name; UTF encodings and installed code pages are supported. Invalid character sequences fail. |
| `DetectEncodingFromByteOrderMarks` | `true` | Source-reader option; does not change target encoding. |
| `EmitByteOrderMark` | `false` | Emit the selected encoding's preamble when enabled. |
| `HasHeaderRecord` | `true` | Source-reader option; target header output is controlled by `WriteHeaderRecord`. |
| `WriteHeaderRecord` | `true` | Include one configured column-name header at the beginning of the output file. |
| `Headers` | `null` | Source-reader option for inputs without a header row. |
| `Columns` | `null` | Source projection; not a target schema. |
| `ColumnTypes` | `{}` | Source-reader conversion map only; target output is formatted from the mapped record values. |
| `BinaryColumns` | `[]` (auto-detect) | When set, restrict sidecar output to these top-level binary fields; each must be included in `ColumnsByStep`. |
| `NullValues` | `[]` | Source values treated as null. |
| `NullValue` | `""` | Text written for a null field. |
| `EmptyFieldAsNull` | `false` | Source-reader null handling. |
| `Culture` | `InvariantCulture` | Culture used when parsing source CSV; target values are written from their mapped JSON text. |
| `NewLine` | LF (`\n`) | Output record separator. Allowed: LF, CR, CRLF. |
| `IgnoreBlankLines` | `true` | Source-reader behavior. |
| `AllowComments` | `false` | Source-reader comment-line handling. |
| `Comment` | `#` | One-character source comment marker. |
| `DetectColumnCountChanges` | `true` | Source-reader field-count validation. |
| `TrimOptions` | `None` | Parser trim behavior: `None`, `Trim`, or `InsideQuotes`. |
| `QuoteAllFields` | `false` | Quote every output field. |
| `ColumnsByStep` | `null` | Required output schema: step ID maps to ordered output field names. |
| `SourceColumnsByStep` | `null` | Fixture-conversion helper schema; not used by the runtime CSV target. |

`ConnectionId` must resolve to a `DirectoryInfo` or `FileInfo`. With a directory, `Path` names the exact output file relative to that directory; with a `FileInfo`, omit `Path`. `Format.Options.ColumnsByStep` must contain a non-empty, unique column list for each step written by the CSV target. A missing target step entry fails finalization. The target serializes mapped records in the declared column order and atomically replaces its destination during the scheduler's completion phase; multiple file targets are not a cross-file transaction.

| Logical mapped value | CSV output behavior |
|---|---|
| Null | Written as `NullValue`. |
| Boolean, integer, number, text, GUID, date/time | Written as one escaped CSV text field using the value's JSON text; GUID and date/time values are text. |
| Object / array | Written as JSON text in one field. |
| `MigrationBinaryValue`, `byte[]`, readable `Stream` | Written as a relative sidecar reference (not Base64) when top-level in a selected column. |

See [binary values and sidecar files](../../concepts/binary-values-and-sidecars.md) for round-trip configuration.
