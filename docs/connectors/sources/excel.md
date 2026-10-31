# Excel source

The `file` source with `Format.Type = 'excel'` reads rows from an Excel workbook (`.xlsx` or `.xls`). Excel is source-only. Use a `DirectoryInfo` connection plus a relative `Path`, or a `FileInfo` connection that names the exact workbook.

## Configuration

| Property | Required | Description |
|---|---:|---|
| `Type` | Yes | `file`. |
| `Format.Type` | Yes | `excel`. |
| `ConnectionId` | Yes | ID whose runtime value is a `System.IO.DirectoryInfo` or `System.IO.FileInfo`. |
| `Path` | Conditional | Workbook filename relative to a `DirectoryInfo`; omit when the connection is a `FileInfo`. |
| `Key` | Yes | Source property used as the row key. |
| `Id` | No | Optional source property copied to the row's `Id`. |
| `ExpectedRecordCount` | No | Positive expected number of records; `0` (default) counts rows by reading the selected sheet. |
| `Format.Options` | No | Workbook, header, column, and type settings. |

### Format options

| Option | Default | Description |
|---|---:|---|
| `Sheet` | First worksheet | Worksheet name. The name comparison is case-insensitive. |
| `HeaderRow` | `1` | One-based row containing headers, or the first data row when `HasHeaderRecord` is `false`. |
| `HasHeaderRecord` | `true` | Read column names from `HeaderRow`. When `false`, `Columns` is required. |
| `Columns` | All header columns | Optional ordered subset of columns to emit. |
| `ColumnTypes` | Empty | Optional column-to-type map. Supported types match the CSV connector: `bool`, integer types, floating-point types, `decimal`, `guid`, `date`, `datetime`, `datetimeoffset`, `json`, and `binary`. |
| `SkipEmptyRows` | `true` | Skip rows where every selected cell is empty. |
| `EmptyValueAsNull` | `true` | Convert empty string cells to `null`. |

Header names must be non-empty and unique. The engine reads the selected sheet, sorts rows by the configured key, and then streams them through the standard JSONL reader. The whole selected sheet is materialized during source initialization, so very large workbooks should be split or exported to CSV/Parquet first.

| Logical cell value | Type in the PowerShell mapping |
|---|---|
| Default text / blank | `System.String` / `$null` |
| Boolean | `System.Boolean` |
| Numeric cell | `System.Double` after JSON transport |
| Date/time cell | `System.DateTime` for ISO-formatted values after default PowerShell JSON parsing |
| `ColumnTypes` integer / number | JSON integer (`Int64`) or fractional number (`Double`) |
| `ColumnTypes` `guid` | `System.String` |
| `ColumnTypes` `date`, `datetime`, `datetimeoffset` | `System.DateTime` for ISO-formatted values after default PowerShell JSON parsing |
| `ColumnTypes` `json`, `binary` | Hashtable / array / scalar, or Base64 string |

`ColumnTypes` selects the reader conversion; JSON transport determines the concrete mapping value. Excel has no native binary-cell representation.

## Example

```powershell
$steps = @(
    @{
        Id = 'Contacts'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'file'
            Format = @{
                Type = 'excel'
                Options = @{
                    Sheet = 'Contacts'
                    Columns = @('ContactId', 'FirstName', 'LastName', 'CreatedOn')
                    ColumnTypes = @{
                        ContactId = 'guid'
                        CreatedOn = 'datetime'
                    }
                }
            }
            ConnectionId = 'files'
            Path = 'contacts.xlsx'
            Key = 'ContactId'
        }
    }
)

$connections = @{
    files = [System.IO.DirectoryInfo]'/data/import'
}
```

See [source connectors](./README.md) and [migration step configuration](../../configuration.md).
