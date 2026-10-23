# CSV target

The `csv` target writes one CSV file per batch. Configure `ColumnsByStep` to define the output columns and their order for every CSV target step. Option names are case-insensitive when deserialized.

## Configuration example

```powershell
Target = @{
    Type = 'csv'
    ConnectionId = 'output-files'
    Path = 'customers'
    Operation = 'Write'
    Options = @{
        ColumnsByStep = @{
            Customers = @('Id', 'name', 'category')
        }
        WriteHeaderRecord = $true
        NullValue = ''
        QuoteAllFields = $false
    }
}
```

## Options

Defaults are shown below. `ColumnTypes` accepts `string` (default), `bool`/`boolean`, `byte`, `short`/`int16`, `int`/`int32`, `long`/`int64`, `float`/`single`, `double`, `decimal`, `guid`, `date`, `datetime`, `datetimeoffset`, `json`, and `binary`.

| Option | Default | Target behavior |
|---|---|---|
| `Delimiter` | `,` | Field separator; must be non-empty. |
| `Quote` | `"` | One-character quote delimiter. |
| `Escape` | `"` | One-character escape character. |
| `Encoding` | `utf-8` | .NET encoding name; UTF encodings and installed code pages are supported. Invalid character sequences fail. |
| `DetectEncodingFromByteOrderMarks` | `true` | Source-reader option; does not change target encoding. |
| `EmitByteOrderMark` | `false` | Emit the selected encoding's preamble when enabled. |
| `HasHeaderRecord` | `true` | Source-reader option; target header output is controlled by `WriteHeaderRecord`. |
| `WriteHeaderRecord` | `true` | Include the configured column names in each batch file. |
| `Headers` | `null` | Source-reader option for inputs without a header row. |
| `Columns` | `null` | Source projection; not a target schema. |
| `ColumnTypes` | `{}` | Source conversion map; target fields are formatted from mapped values. |
| `NullValues` | `[]` | Source values treated as null. |
| `NullValue` | `""` | Text written for a null field. |
| `EmptyFieldAsNull` | `false` | Source-reader null handling. |
| `Culture` | `InvariantCulture` | Culture used by the CSV parser and writer. |
| `NewLine` | LF (`\n`) | Output record separator. Allowed: LF, CR, CRLF. |
| `IgnoreBlankLines` | `true` | Source-reader behavior. |
| `AllowComments` | `false` | Source-reader comment-line handling. |
| `Comment` | `#` | One-character source comment marker. |
| `DetectColumnCountChanges` | `true` | Source-reader field-count validation. |
| `TrimOptions` | `None` | Parser trim behavior: `None`, `Trim`, or `InsideQuotes`. |
| `QuoteAllFields` | `false` | Quote every output field. |
| `ColumnsByStep` | `null` | Required output schema: step ID maps to ordered output field names. |
| `SourceColumnsByStep` | `null` | Fixture-conversion helper schema; not used by the runtime CSV target. |

`ColumnsByStep` must contain a non-empty, unique column list for each step written by the CSV target. A missing target step entry fails the write. The target serializes mapped records in the declared column order.
