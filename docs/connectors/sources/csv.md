# CSV source

The `csv` source reads records incrementally using CSV parsing rules. Option names are case-insensitive when deserialized. A `DirectoryInfo` connection supplies the root directory and requires a relative `Path`; a `FileInfo` names the exact input file and does not use `Path`.

## Configuration example

```powershell
Source = @{
    Type = 'csv'
    ConnectionId = 'input-files'
    Path = 'customers.csv'
    Key = 'SourceId'
    Id = 'DataverseId'
    Options = @{
        Delimiter = ','
        HasHeaderRecord = $true
        Columns = @('SourceId', 'DataverseId', 'Name')
        ColumnTypes = @{ DataverseId = 'guid' }
        EmptyFieldAsNull = $true
    }
}
```

If `HasHeaderRecord` is false, declare the input column names using `Headers`. `Columns` projects the source to selected fields; omitted or null means all columns. `ColumnTypes` converts selected input fields. Null handling and culture affect parsing. Invalid encoding, malformed values, or inconsistent field counts fail the read.

The source detects the file's record separator (`LF`, `CRLF`, or `CR`) before parsing, independent of the operating system. This accepts CSV files produced by Windows tools such as `Export-Csv` even when the connector's `NewLine` default is `LF`; `NewLine` controls writer output, not source parsing.

## Options

Defaults are shown below. `ColumnTypes` accepts `string` (default), `bool`/`boolean`, `byte`, `short`/`int16`, `int`/`int32`, `long`/`int64`, `float`/`single`, `double`, `decimal`, `guid`, `date`, `datetime`, `datetimeoffset`, `json`, and `binary`.

| Option | Default | Source behavior |
|---|---|---|
| `Delimiter` | `,` | Field separator; must be non-empty. |
| `Quote` | `"` | One-character quote delimiter. |
| `Escape` | `"` | One-character escape character. |
| `Encoding` | `utf-8` | .NET encoding name; UTF encodings and installed code pages are supported. Invalid sequences fail rather than being replaced. |
| `DetectEncodingFromByteOrderMarks` | `true` | Detect a source-file BOM. |
| `EmitByteOrderMark` | `false` | Writer option; does not change source decoding. |
| `HasHeaderRecord` | `true` | Read the first record as headers. When false, `Headers` is required. |
| `WriteHeaderRecord` | `true` | Writer option; does not change source parsing. |
| `Headers` | `null` | Declared names when the file has no header row. |
| `Columns` | `null` | Projection; omitted means all input columns. |
| `ColumnTypes` | `{}` | Case-insensitive field-to-type conversions. |
| `NullValues` | `[]` | Exact source values treated as null. |
| `NullValue` | `""` | Writer representation for null; does not affect source null detection. |
| `EmptyFieldAsNull` | `false` | Treat empty input fields as null. |
| `Culture` | `InvariantCulture` | Culture used for culture-sensitive numeric and date conversions. |
| `NewLine` | LF (`\n`) | Writer record separator. Allowed: LF, CR, CRLF. |
| `IgnoreBlankLines` | `true` | Ignore blank input lines. |
| `AllowComments` | `false` | Enable comment-line handling. |
| `Comment` | `#` | One-character comment marker. |
| `DetectColumnCountChanges` | `true` | Fail if row field counts differ from header/configuration. |
| `TrimOptions` | `None` | CSV trim behavior: `None`, `Trim`, or `InsideQuotes`. |
| `QuoteAllFields` | `false` | Writer option; quote all output fields. |
| `ColumnsByStep` | `null` | Writer schema; required for a CSV target, not used by a source. |
| `SourceColumnsByStep` | `null` | Fixture-conversion helper schema; not used by the runtime CSV source reader. |

`Headers`, `Columns`, and each step-specific column list must contain unique, non-empty names. `SourceColumnsByStep` is not a runtime source schema; use `Headers` and `Columns` for external CSV input.
